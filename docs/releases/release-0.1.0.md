Initial JPEG XL plugin scaffold for PictureSharp (.NET 10 / C# 14).

- Registration through JxlFormat and ImageFormat.Jxl.
- Raw codestream and container signature detection.
- Tests, API snapshot, package consumer, trimming and Linux x64 Native AOT checks.
- PictureSharp >= 0.8.2; pure managed code.

This is a pre-release scaffold: Identify, pixel decoding, encoding and metadata operations
are not implemented. The registration placeholder makes CanDecode report true; calling
Load or Identify throws NotSupportedException. Detection does not validate a file.

Packages are attached to this private release only, not published to a package registry.
