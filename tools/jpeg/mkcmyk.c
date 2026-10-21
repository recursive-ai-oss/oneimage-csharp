/* Writes a 4-component JPEG from raw interleaved samples with libjpeg-turbo, for the CMYK/YCCK tests.
 * usage: mkcmyk in.raw width height cmyk|ycck quality out.jpg
 * libjpeg adds the Adobe marker itself for these color spaces; samples are stored as given. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <jpeglib.h>

int main(int argc, char **argv)
{
  struct jpeg_compress_struct cinfo;
  struct jpeg_error_mgr jerr;
  if (argc < 7) return 2;
  int w = atoi(argv[2]), h = atoi(argv[3]);
  FILE *in = fopen(argv[1], "rb"), *out = fopen(argv[6], "wb");
  if (!in || !out) return 2;
  unsigned char *buf = malloc((size_t)w * h * 4);
  if (fread(buf, 4, (size_t)w * h, in) != (size_t)w * h) return 2;
  cinfo.err = jpeg_std_error(&jerr);
  jpeg_create_compress(&cinfo);
  jpeg_stdio_dest(&cinfo, out);
  cinfo.image_width = w; cinfo.image_height = h; cinfo.input_components = 4;
  cinfo.in_color_space = JCS_CMYK;
  jpeg_set_defaults(&cinfo);
  jpeg_set_colorspace(&cinfo, strcmp(argv[4], "ycck") == 0 ? JCS_YCCK : JCS_CMYK);
  jpeg_set_quality(&cinfo, atoi(argv[5]), TRUE);
  jpeg_start_compress(&cinfo, TRUE);
  while (cinfo.next_scanline < cinfo.image_height) {
    JSAMPROW row = buf + (size_t)cinfo.next_scanline * w * 4;
    jpeg_write_scanlines(&cinfo, &row, 1);
  }
  jpeg_finish_compress(&cinfo);
  jpeg_destroy_compress(&cinfo);
  fclose(out);
  return 0;
}
