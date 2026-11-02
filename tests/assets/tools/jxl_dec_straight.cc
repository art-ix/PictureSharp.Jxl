// jxl_dec_straight: decodes a JPEG XL file like djxl 0.12.0, but with premultiplied alpha converted to straight alpha
// (JxlDecoderSetUnpremultiplyAlpha), which djxl cannot do. A test-corpus oracle tool, not shipped; see
// tests/assets/tools/README.md for the build and docs/design/0.8.16-extra-channels.md.
//
// Usage: jxl_dec_straight [--bits=8|16] [--no_coalescing] <in.jxl> <out-prefix>
//
// Writes <out-prefix>-<n>.pam for every frame the decoder returns (coalesced: every displayed frame; with
// --no_coalescing: every layer at its own size): GRAYSCALE, GRAYSCALE_ALPHA, RGB or RGB_ALPHA as the image has them,
// maxval 255 or 65535 (big endian), orientation applied, spot colours rendered, other extra channels dropped. 8-bit
// output takes the same row conversion and dithering as djxl's 8-bit output.
#include <jxl/decode.h>
#include <jxl/decode_cxx.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

namespace {

[[noreturn]] void Fail(const std::string& message) {
  std::fprintf(stderr, "jxl_dec_straight: %s\n", message.c_str());
  std::exit(1);
}

void WritePam(const std::string& path, size_t width, size_t height, size_t channels, int bits, const std::vector<uint8_t>& data) {
  static const char* kTypes[] = {"", "GRAYSCALE", "GRAYSCALE_ALPHA", "RGB", "RGB_ALPHA"};
  std::ofstream out(path, std::ios::binary);
  out << "P7\nWIDTH " << width << "\nHEIGHT " << height << "\nDEPTH " << channels << "\nMAXVAL " << (bits == 16 ? 65535 : 255)
      << "\nTUPLTYPE " << kTypes[channels] << "\nENDHDR\n";
  out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
  if (!out) Fail("cannot write " + path);
}

}  // namespace

int main(int argc, char** argv) {
  int bits = 8;
  bool coalescing = true;
  std::vector<std::string> paths;
  for (int i = 1; i < argc; i++) {
    std::string arg = argv[i];
    if (arg == "--bits=8") bits = 8;
    else if (arg == "--bits=16") bits = 16;
    else if (arg == "--no_coalescing") coalescing = false;
    else paths.push_back(arg);
  }
  if (paths.size() != 2) Fail("usage: jxl_dec_straight [--bits=8|16] [--no_coalescing] <in.jxl> <out-prefix>");

  std::ifstream in(paths[0], std::ios::binary);
  if (!in) Fail("cannot read " + paths[0]);
  std::vector<uint8_t> file((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());

  JxlDecoderPtr decoder = JxlDecoderMake(nullptr);
  if (JxlDecoderSubscribeEvents(decoder.get(), JXL_DEC_BASIC_INFO | JXL_DEC_FRAME | JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
      JxlDecoderSetUnpremultiplyAlpha(decoder.get(), JXL_TRUE) != JXL_DEC_SUCCESS ||
      JxlDecoderSetRenderSpotcolors(decoder.get(), JXL_TRUE) != JXL_DEC_SUCCESS ||
      JxlDecoderSetCoalescing(decoder.get(), coalescing ? JXL_TRUE : JXL_FALSE) != JXL_DEC_SUCCESS) {
    Fail("decoder setup failed");
  }
  JxlDecoderSetInput(decoder.get(), file.data(), file.size());
  JxlDecoderCloseInput(decoder.get());

  JxlBasicInfo info;
  JxlPixelFormat format = {0, bits == 16 ? JXL_TYPE_UINT16 : JXL_TYPE_UINT8, JXL_BIG_ENDIAN, 0};
  size_t width = 0, height = 0, frame = 0;
  std::vector<uint8_t> pixels;
  for (;;) {
    JxlDecoderStatus status = JxlDecoderProcessInput(decoder.get());
    if (status == JXL_DEC_ERROR) Fail("decoding failed");
    if (status == JXL_DEC_NEED_MORE_INPUT) Fail("truncated file");
    if (status == JXL_DEC_SUCCESS) break;
    if (status == JXL_DEC_BASIC_INFO) {
      if (JxlDecoderGetBasicInfo(decoder.get(), &info) != JXL_DEC_SUCCESS) Fail("no basic info");
      format.num_channels = info.num_color_channels + (info.alpha_bits != 0 ? 1 : 0);
      // Like djxl: the image's size after the orientation is undone.
      width = info.orientation > 4 ? info.ysize : info.xsize;
      height = info.orientation > 4 ? info.xsize : info.ysize;
    } else if (status == JXL_DEC_FRAME) {
      if (!coalescing) {
        JxlFrameHeader header;
        if (JxlDecoderGetFrameHeader(decoder.get(), &header) != JXL_DEC_SUCCESS) Fail("no frame header");
        width = info.orientation > 4 ? header.layer_info.ysize : header.layer_info.xsize;
        height = info.orientation > 4 ? header.layer_info.xsize : header.layer_info.ysize;
      }
    } else if (status == JXL_DEC_NEED_IMAGE_OUT_BUFFER) {
      size_t size = 0;
      if (JxlDecoderImageOutBufferSize(decoder.get(), &format, &size) != JXL_DEC_SUCCESS) Fail("no buffer size");
      if (size != width * height * format.num_channels * (bits / 8)) Fail("unexpected buffer size");
      pixels.assign(size, 0);
      if (JxlDecoderSetImageOutBuffer(decoder.get(), &format, pixels.data(), pixels.size()) != JXL_DEC_SUCCESS) Fail("no buffer");
    } else if (status == JXL_DEC_FULL_IMAGE) {
      WritePam(paths[1] + "-" + std::to_string(frame++) + ".pam", width, height, format.num_channels, bits, pixels);
    }
  }
  return frame == 0 ? 1 : 0;
}
