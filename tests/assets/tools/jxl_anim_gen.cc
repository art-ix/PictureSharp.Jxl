// jxl_anim_gen: writes JPEG XL animations that cjxl cannot produce (loop counts, tick rates, timecodes, names,
// zero-duration frames, crops, blend modes, reference slots) through the libjxl 0.12.0 encoder API. A test-corpus
// tool, not shipped; see tests/assets/tools/README.md for the build and docs/design/0.8.12-animation.md.
//
// Usage: jxl_anim_gen <spec.txt> <out.jxl>
//
// Spec, one directive per line ('#' starts a comment):
//   size W H              canvas size (required)
//   tps NUM DEN           ticks per second as a fraction (default 1000 1)
//   loops N               num_loops, 0 = forever (default 0)
//   timecodes             store a timecode per frame
//   alpha                 an 8-bit (or 16-bit) alpha channel
//   bits N                bits per sample, 8 or 16 (default 8)
//   distance D            lossy VarDCT at this distance (default lossless)
//   orientation N         EXIF orientation 1-8 (default 1)
//   frame key=value ...   one frame: duration (ticks), timecode, name, x0, y0, w, h (crop), blend (replace, add, blend,
//                         muladd, mul), source, save (save_as_reference), seed (pattern), opacity (alpha 0-255)
#include <jxl/encode.h>
#include <jxl/encode_cxx.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <vector>

namespace {

struct Frame {
  std::map<std::string, std::string> values;
  long Get(const std::string& key, long fallback) const {
    auto it = values.find(key);
    return it == values.end() ? fallback : std::stol(it->second);
  }
};

[[noreturn]] void Fail(const std::string& message) {
  std::fprintf(stderr, "jxl_anim_gen: %s\n", message.c_str());
  std::exit(1);
}

void Check(JxlEncoderStatus status, const char* what) {
  if (status != JXL_ENC_SUCCESS) Fail(std::string("libjxl failed: ") + what);
}

JxlBlendMode BlendMode(const std::string& name) {
  if (name == "replace") return JXL_BLEND_REPLACE;
  if (name == "add") return JXL_BLEND_ADD;
  if (name == "blend") return JXL_BLEND_BLEND;
  if (name == "muladd") return JXL_BLEND_MULADD;
  if (name == "mul") return JXL_BLEND_MUL;
  Fail("unknown blend mode " + name);
}

// A deterministic pattern: gradients shifted by the seed, alpha from the opacity with a diagonal ramp.
std::vector<uint8_t> Pixels(size_t width, size_t height, bool alpha, int bits, long seed, long opacity) {
  size_t channels = alpha ? 4 : 3, bytes = bits == 16 ? 2 : 1;
  std::vector<uint8_t> data(width * height * channels * bytes);
  for (size_t y = 0; y < height; y++) {
    for (size_t x = 0; x < width; x++) {
      uint32_t v[4] = {
          static_cast<uint32_t>((x * 4093 / (width > 1 ? width - 1 : 1) + seed * 9001) % 4096),
          static_cast<uint32_t>((y * 4093 / (height > 1 ? height - 1 : 1) + seed * 5003) % 4096),
          static_cast<uint32_t>(((x + y) * 1531 + seed * 2477) % 4096),
          static_cast<uint32_t>(opacity * 16 + ((x + y + seed) % 8))};
      for (size_t c = 0; c < channels; c++) {
        uint32_t sample = v[c] > 4095 ? 4095 : v[c];
        size_t offset = ((y * width + x) * channels + c) * bytes;
        if (bits == 16) {
          uint32_t wide = sample * 16 + (sample >> 8);
          data[offset] = static_cast<uint8_t>(wide & 255);  // little endian
          data[offset + 1] = static_cast<uint8_t>(wide >> 8);
        } else {
          data[offset] = static_cast<uint8_t>(sample >> 4);
        }
      }
    }
  }
  return data;
}

}  // namespace

int main(int argc, char** argv) {
  if (argc != 3) Fail("usage: jxl_anim_gen <spec.txt> <out.jxl>");
  std::ifstream spec(argv[1]);
  if (!spec) Fail(std::string("cannot read ") + argv[1]);

  size_t width = 0, height = 0;
  uint32_t tps_num = 1000, tps_den = 1, loops = 0;
  bool timecodes = false, alpha = false;
  int bits = 8, orientation = 1;
  float distance = 0;
  std::vector<Frame> frames;
  for (std::string line; std::getline(spec, line);) {
    line = line.substr(0, line.find('#'));
    std::istringstream words(line);
    std::string key;
    if (!(words >> key)) continue;
    if (key == "size") {
      words >> width >> height;
    } else if (key == "tps") {
      words >> tps_num >> tps_den;
    } else if (key == "loops") {
      words >> loops;
    } else if (key == "timecodes") {
      timecodes = true;
    } else if (key == "alpha") {
      alpha = true;
    } else if (key == "bits") {
      words >> bits;
    } else if (key == "distance") {
      words >> distance;
    } else if (key == "orientation") {
      words >> orientation;
    } else if (key == "frame") {
      Frame frame;
      for (std::string pair; words >> pair;) {
        size_t eq = pair.find('=');
        if (eq == std::string::npos) Fail("expected key=value: " + pair);
        frame.values[pair.substr(0, eq)] = pair.substr(eq + 1);
      }
      frames.push_back(frame);
    } else {
      Fail("unknown directive " + key);
    }
  }
  if (width == 0 || height == 0 || frames.empty()) Fail("size and at least one frame are required");
  if (bits != 8 && bits != 16) Fail("bits must be 8 or 16");

  JxlEncoderPtr encoder = JxlEncoderMake(nullptr);
  JxlBasicInfo info;
  JxlEncoderInitBasicInfo(&info);
  info.xsize = static_cast<uint32_t>(width);
  info.ysize = static_cast<uint32_t>(height);
  info.bits_per_sample = bits;
  info.num_color_channels = 3;
  info.uses_original_profile = distance == 0 ? JXL_TRUE : JXL_FALSE;
  if (alpha) {
    info.num_extra_channels = 1;
    info.alpha_bits = bits;
  }
  info.orientation = static_cast<JxlOrientation>(orientation);
  info.have_animation = JXL_TRUE;
  info.animation.tps_numerator = tps_num;
  info.animation.tps_denominator = tps_den;
  info.animation.num_loops = loops;
  info.animation.have_timecodes = timecodes ? JXL_TRUE : JXL_FALSE;
  Check(JxlEncoderSetBasicInfo(encoder.get(), &info), "basic info");
  JxlColorEncoding color;
  JxlColorEncodingSetToSRGB(&color, JXL_FALSE);
  Check(JxlEncoderSetColorEncoding(encoder.get(), &color), "colour encoding");

  JxlEncoderFrameSettings* settings = JxlEncoderFrameSettingsCreate(encoder.get(), nullptr);
  if (distance == 0) {
    Check(JxlEncoderSetFrameLossless(settings, JXL_TRUE), "lossless");
  } else {
    Check(JxlEncoderSetFrameDistance(settings, distance), "distance");
  }
  Check(JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EFFORT, 7), "effort");

  JxlPixelFormat format = {alpha ? 4u : 3u, bits == 16 ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8, JXL_LITTLE_ENDIAN, 0};
  for (size_t i = 0; i < frames.size(); i++) {
    const Frame& frame = frames[i];
    JxlFrameHeader header;
    JxlEncoderInitFrameHeader(&header);
    header.duration = static_cast<uint32_t>(frame.Get("duration", 0));
    header.timecode = static_cast<uint32_t>(frame.Get("timecode", 0));
    size_t w = static_cast<size_t>(frame.Get("w", static_cast<long>(width)));
    size_t h = static_cast<size_t>(frame.Get("h", static_cast<long>(height)));
    long x0 = frame.Get("x0", 0), y0 = frame.Get("y0", 0);
    if (x0 != 0 || y0 != 0 || w != width || h != height) {
      header.layer_info.have_crop = JXL_TRUE;
      header.layer_info.crop_x0 = static_cast<int32_t>(x0);
      header.layer_info.crop_y0 = static_cast<int32_t>(y0);
      header.layer_info.xsize = static_cast<uint32_t>(w);
      header.layer_info.ysize = static_cast<uint32_t>(h);
    }
    auto blend = frame.values.find("blend");
    header.layer_info.blend_info.blendmode = blend == frame.values.end() ? JXL_BLEND_REPLACE : BlendMode(blend->second);
    header.layer_info.blend_info.source = static_cast<uint32_t>(frame.Get("source", 0));
    header.layer_info.blend_info.alpha = 0;
    header.layer_info.save_as_reference = static_cast<uint32_t>(frame.Get("save", 0));
    Check(JxlEncoderSetFrameHeader(settings, &header), "frame header");
    if (alpha) {
      JxlBlendInfo alpha_blend;
      JxlEncoderInitBlendInfo(&alpha_blend);
      alpha_blend.blendmode = header.layer_info.blend_info.blendmode;
      alpha_blend.source = header.layer_info.blend_info.source;
      Check(JxlEncoderSetExtraChannelBlendInfo(settings, 0, &alpha_blend), "alpha blend info");
    }
    auto name = frame.values.find("name");
    if (name != frame.values.end()) Check(JxlEncoderSetFrameName(settings, name->second.c_str()), "frame name");
    std::vector<uint8_t> pixels = Pixels(w, h, alpha, bits, frame.Get("seed", static_cast<long>(i)), frame.Get("opacity", 255));
    Check(JxlEncoderAddImageFrame(settings, &format, pixels.data(), pixels.size()), "image frame");
  }
  JxlEncoderCloseInput(encoder.get());

  std::vector<uint8_t> output(1 << 16);
  uint8_t* next = output.data();
  size_t available = output.size();
  JxlEncoderStatus status;
  while ((status = JxlEncoderProcessOutput(encoder.get(), &next, &available)) == JXL_ENC_NEED_MORE_OUTPUT) {
    size_t used = next - output.data();
    output.resize(output.size() * 2);
    next = output.data() + used;
    available = output.size() - used;
  }
  Check(status, "output");
  output.resize(next - output.data());
  std::ofstream out(argv[2], std::ios::binary);
  out.write(reinterpret_cast<const char*>(output.data()), static_cast<std::streamsize>(output.size()));
  return out ? 0 : 1;
}
