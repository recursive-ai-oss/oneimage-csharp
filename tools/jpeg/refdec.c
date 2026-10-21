/* Reference decode for the JPEG tests: decodes a file with libjpeg-turbo (accurate integer DCT, fancy upsampling,
 * the library's default color handling) and writes the raw samples. 8-bit data is written as bytes, 12-bit as
 * little-endian 16-bit words holding the 12-bit values.
 * usage: refdec in.jpg out.raw [scale_denom]   (prints "width height components precision" to stdout) */
#include <stdio.h>
#include <stdlib.h>
#include <jpeglib.h>

int main(int argc, char **argv)
{
  struct jpeg_decompress_struct cinfo;
  struct jpeg_error_mgr jerr;
  FILE *in, *out;
  int denom = argc > 3 ? atoi(argv[3]) : 1;
  if (argc < 3) return 2;
  in = fopen(argv[1], "rb");
  out = fopen(argv[2], "wb");
  if (!in || !out) return 2;
  cinfo.err = jpeg_std_error(&jerr);
  jpeg_create_decompress(&cinfo);
  jpeg_stdio_src(&cinfo, in);
  jpeg_read_header(&cinfo, TRUE);
  cinfo.scale_num = 1;
  cinfo.scale_denom = denom;
  jpeg_start_decompress(&cinfo);
  int prec = cinfo.data_precision;
  int stride = cinfo.output_width * cinfo.output_components;
  printf("%u %u %d %d\n", cinfo.output_width, cinfo.output_height, cinfo.output_components, prec);
  if (prec == 8) {
    JSAMPROW row = malloc(stride);
    while (cinfo.output_scanline < cinfo.output_height) {
      jpeg_read_scanlines(&cinfo, &row, 1);
      fwrite(row, 1, stride, out);
    }
  } else {
    J12SAMPROW row = malloc(stride * sizeof(J12SAMPLE));
    while (cinfo.output_scanline < cinfo.output_height) {
      jpeg12_read_scanlines(&cinfo, &row, 1);
      for (int i = 0; i < stride; i++) { putc(row[i] & 255, out); putc(row[i] >> 8, out); }
    }
  }
  jpeg_finish_decompress(&cinfo);
  jpeg_destroy_decompress(&cinfo);
  fclose(out);
  return 0;
}
