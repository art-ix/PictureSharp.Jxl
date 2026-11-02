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
//   alpha [premultiplied] an 8-bit (or 16-bit) alpha channel, optionally associated (colour stored premultiplied)
//   gray                  one colour channel instead of three
//   still                 no animation header: the frames are layers of one image (durations are ignored)
//   extra TYPE key=value  a further extra channel after the alpha channel (0.8.16): TYPE is alpha, depth, spot,
//                         selection, black, thermal, unknown or optional; bits (8 or 16, default the image's), float
//                         (32-bit float samples), color=r,g,b,a (spot colour and opacity), name, premultiplied (alpha)
//   ec_resampling N       extra channels coded at 1/N resolution (1, 2, 4 or 8) and upsampled by the decoder
//   bits N                bits per sample, 8 or 16 (default 8)
//   distance D            lossy VarDCT at this distance (default lossless)
//   orientation N         EXIF orientation 1-8 (default 1)
//   frame key=value ...   one frame: duration (ticks), timecode, name, x0, y0, w, h (crop), blend (replace, add, blend,
//                         muladd, mul), source, save (save_as_reference), seed (pattern), opacity (alpha 0-255),
//                         ramp=1 (alpha rising from 0 across the frame, every 5th column 0)
#include <jxl/encode.h>
#include <jxl/encode_cxx.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
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

struct Extra {
  JxlExtraChannelType type = JXL_CHANNEL_UNKNOWN;
  int bits = 0;
  bool is_float = false, premultiplied = false;
  float color[4] = {0, 0, 0, 0};
  std::string name;
};

JxlExtraChannelType ExtraType(const std::string& name) {
  if (name == "alpha") return JXL_CHANNEL_ALPHA;
  if (name == "depth") return JXL_CHANNEL_DEPTH;
  if (name == "spot") return JXL_CHANNEL_SPOT_COLOR;
  if (name == "selection") return JXL_CHANNEL_SELECTION_MASK;
  if (name == "black") return JXL_CHANNEL_BLACK;
  if (name == "thermal") return JXL_CHANNEL_THERMAL;
  if (name == "unknown") return JXL_CHANNEL_UNKNOWN;
  if (name == "optional") return JXL_CHANNEL_OPTIONAL;
  Fail("unknown extra channel type " + name);
}

JxlBlendMode BlendMode(const std::string& name) {
  if (name == "replace") return JXL_BLEND_REPLACE;
  if (name == "add") return JXL_BLEND_ADD;
  if (name == "blend") return JXL_BLEND_BLEND;
  if (name == "muladd") return JXL_BLEND_MULADD;
  if (name == "mul") return JXL_BLEND_MUL;
  Fail("unknown blend mode " + name);
}

void Store(std::vector<uint8_t>& data, size_t offset, uint32_t sample, int bits) {
  if (bits == 16) {
    uint32_t wide = sample * 16 + (sample >> 8);
    data[offset] = static_cast<uint8_t>(wide & 255);  // little endian
    data[offset + 1] = static_cast<uint8_t>(wide >> 8);
  } else {
    data[offset] = static_cast<uint8_t>(sample >> 4);
  }
}

// A deterministic pattern: gradients shifted by the seed, alpha from the opacity with a diagonal ramp. Premultiplied
// colour is the straight colour scaled by alpha (12-bit samples), so alpha 0 has colour 0.
std::vector<uint8_t> Pixels(size_t width, size_t height, bool gray, bool alpha, bool premultiplied, int bits, long seed,
                            long opacity, bool ramp) {
  size_t colors = gray ? 1 : 3, channels = colors + (alpha ? 1 : 0), bytes = bits == 16 ? 2 : 1;
  std::vector<uint8_t> data(width * height * channels * bytes);
  for (size_t y = 0; y < height; y++) {
    for (size_t x = 0; x < width; x++) {
      uint32_t v[4] = {
          static_cast<uint32_t>((x * 4093 / (width > 1 ? width - 1 : 1) + seed * 9001) % 4096),
          static_cast<uint32_t>((y * 4093 / (height > 1 ? height - 1 : 1) + seed * 5003) % 4096),
          static_cast<uint32_t>(((x + y) * 1531 + seed * 2477) % 4096),
          static_cast<uint32_t>(opacity * 16 + ((x + y + seed) % 8))};
      if (ramp) v[3] = x % 5 == 0 ? 0 : static_cast<uint32_t>((x * 4095 / (width > 1 ? width - 1 : 1) + y * 37) % 4096);
      uint32_t a = v[3] > 4095 ? 4095 : v[3];
      for (size_t c = 0; c < channels; c++) {
        uint32_t sample = c == colors ? a : v[c] > 4095 ? 4095 : v[c];
        if (c < colors && alpha && premultiplied) sample = (sample * a + 2047) / 4095;
        Store(data, ((y * width + x) * channels + c) * bytes, sample, bits);
      }
    }
  }
  return data;
}

// One extra channel: diagonal bands and a disc, different per channel and seed; float channels get v / 4095.
std::vector<uint8_t> ExtraPixels(size_t width, size_t height, const Extra& extra, size_t index, long seed) {
  size_t bytes = extra.is_float ? 4 : extra.bits == 16 ? 2 : 1;
  std::vector<uint8_t> data(width * height * bytes);
  long cx = static_cast<long>(width) / 3 + seed % 5, cy = static_cast<long>(height) / 2, r = static_cast<long>(width) / 4 + 1;
  for (size_t y = 0; y < height; y++) {
    for (size_t x = 0; x < width; x++) {
      long dx = static_cast<long>(x) - cx, dy = static_cast<long>(y) - cy;
      uint32_t band = static_cast<uint32_t>((x * 97 * (index + 1) + y * 53 + seed * 1237 + index * 911) % 4096);
      uint32_t sample = dx * dx + dy * dy <= r * r ? 4095 - band / 4 : band / 2;
      if (extra.is_float) {
        float value = sample / 4095.0f;
        std::memcpy(&data[(y * width + x) * 4], &value, 4);
      } else {
        Store(data, (y * width + x) * bytes, sample, extra.bits);
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
  bool timecodes = false, alpha = false, premultiplied = false, gray = false, still = false;
  int bits = 8, orientation = 1, ec_resampling = 1;
  std::vector<Extra> extras;
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
      std::string word;
      premultiplied = (words >> word) && word == "premultiplied";
    } else if (key == "gray") {
      gray = true;
    } else if (key == "still") {
      still = true;
    } else if (key == "ec_resampling") {
      words >> ec_resampling;
    } else if (key == "extra") {
      Extra extra;
      std::string type;
      words >> type;
      extra.type = ExtraType(type);
      for (std::string pair; words >> pair;) {
        size_t eq = pair.find('=');
        std::string name = pair.substr(0, eq), value = eq == std::string::npos ? "" : pair.substr(eq + 1);
        if (name == "bits") {
          extra.bits = std::stoi(value);
        } else if (name == "float") {
          extra.is_float = true;
        } else if (name == "premultiplied") {
          extra.premultiplied = true;
        } else if (name == "name") {
          extra.name = value;
        } else if (name == "color") {
          if (std::sscanf(value.c_str(), "%f,%f,%f,%f", &extra.color[0], &extra.color[1], &extra.color[2], &extra.color[3]) != 4) {
            Fail("color needs r,g,b,a: " + value);
          }
        } else {
          Fail("unknown extra channel key " + name);
        }
      }
      extras.push_back(extra);
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
  for (Extra& extra : extras) {
    if (extra.bits == 0) extra.bits = bits;
    if (!extra.is_float && extra.bits != 8 && extra.bits != 16) Fail("extra channel bits must be 8 or 16");
  }

  JxlEncoderPtr encoder = JxlEncoderMake(nullptr);
  JxlBasicInfo info;
  JxlEncoderInitBasicInfo(&info);
  info.xsize = static_cast<uint32_t>(width);
  info.ysize = static_cast<uint32_t>(height);
  info.bits_per_sample = bits;
  info.num_color_channels = gray ? 1 : 3;
  info.uses_original_profile = distance == 0 ? JXL_TRUE : JXL_FALSE;
  size_t first_extra = alpha ? 1 : 0;
  info.num_extra_channels = static_cast<uint32_t>(first_extra + extras.size());
  if (alpha) {
    info.alpha_bits = bits;
    info.alpha_premultiplied = premultiplied ? JXL_TRUE : JXL_FALSE;
  }
  info.orientation = static_cast<JxlOrientation>(orientation);
  info.have_animation = still ? JXL_FALSE : JXL_TRUE;
  info.animation.tps_numerator = tps_num;
  info.animation.tps_denominator = tps_den;
  info.animation.num_loops = loops;
  info.animation.have_timecodes = timecodes ? JXL_TRUE : JXL_FALSE;
  Check(JxlEncoderSetBasicInfo(encoder.get(), &info), "basic info");
  for (size_t e = 0; e < extras.size(); e++) {
    const Extra& extra = extras[e];
    JxlExtraChannelInfo channel;
    JxlEncoderInitExtraChannelInfo(extra.type, &channel);
    channel.bits_per_sample = extra.is_float ? 32 : extra.bits;
    channel.exponent_bits_per_sample = extra.is_float ? 8 : 0;
    channel.alpha_premultiplied = extra.premultiplied ? JXL_TRUE : JXL_FALSE;
    std::memcpy(channel.spot_color, extra.color, sizeof(extra.color));
    uint32_t index = static_cast<uint32_t>(first_extra + e);
    Check(JxlEncoderSetExtraChannelInfo(encoder.get(), index, &channel), "extra channel info");
    if (!extra.name.empty()) {
      Check(JxlEncoderSetExtraChannelName(encoder.get(), index, extra.name.c_str(), extra.name.size()), "extra channel name");
    }
  }
  JxlColorEncoding color;
  JxlColorEncodingSetToSRGB(&color, gray ? JXL_TRUE : JXL_FALSE);
  Check(JxlEncoderSetColorEncoding(encoder.get(), &color), "colour encoding");

  JxlEncoderFrameSettings* settings = JxlEncoderFrameSettingsCreate(encoder.get(), nullptr);
  if (distance == 0) {
    Check(JxlEncoderSetFrameLossless(settings, JXL_TRUE), "lossless");
  } else {
    Check(JxlEncoderSetFrameDistance(settings, distance), "distance");
  }
  Check(JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EFFORT, 7), "effort");
  if (ec_resampling != 1) {
    Check(JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EXTRA_CHANNEL_RESAMPLING, ec_resampling), "ec resampling");
  }

  JxlPixelFormat format = {(gray ? 1u : 3u) + (alpha ? 1u : 0u), bits == 16 ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8, JXL_LITTLE_ENDIAN, 0};
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
    for (size_t c = 0; c < first_extra + extras.size(); c++) {
      JxlBlendInfo extra_blend;
      JxlEncoderInitBlendInfo(&extra_blend);
      extra_blend.blendmode = header.layer_info.blend_info.blendmode;
      extra_blend.source = header.layer_info.blend_info.source;
      Check(JxlEncoderSetExtraChannelBlendInfo(settings, static_cast<uint32_t>(c), &extra_blend), "extra channel blend info");
    }
    auto name = frame.values.find("name");
    if (name != frame.values.end()) Check(JxlEncoderSetFrameName(settings, name->second.c_str()), "frame name");
    long seed = frame.Get("seed", static_cast<long>(i));
    std::vector<uint8_t> pixels = Pixels(w, h, gray, alpha, premultiplied, bits, seed, frame.Get("opacity", 255),
                                         frame.Get("ramp", 0) != 0);
    Check(JxlEncoderAddImageFrame(settings, &format, pixels.data(), pixels.size()), "image frame");
    for (size_t e = 0; e < extras.size(); e++) {
      std::vector<uint8_t> samples = ExtraPixels(w, h, extras[e], e, seed);
      JxlPixelFormat one = {1, extras[e].is_float ? JXL_TYPE_FLOAT : extras[e].bits == 16 ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8,
                            JXL_LITTLE_ENDIAN, 0};
      Check(JxlEncoderSetExtraChannelBuffer(settings, &one, samples.data(), samples.size(), static_cast<uint32_t>(first_extra + e)),
            "extra channel buffer");
    }
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
