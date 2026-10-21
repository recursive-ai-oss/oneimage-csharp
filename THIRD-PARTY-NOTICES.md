# Third-party notices

## FOGRA39 characterization data

`data/FOGRA39.txt` is the unmodified FOGRA39 characterization data, copied from the ICC characterization data registry
(https://registry.color.org/cmyk-registry/chardata/FOGRA39.txt). Originator: Fogra, www.fogra.org.

Fogra's terms of use permit free use and redistribution of the characterization data, including as part of commercial and
non-commercial software, provided that the data are distributed unmodified and Fogra is identified as the source. The
designation FOGRAxx may be used solely to identify the respective reference data. Such use does not imply certification,
approval or endorsement by Fogra.

The library's generic CMYK profile is derived from this data by `tools/GenericCmyk`. It is this project's own profile, named
after this library; it is not a Fogra profile and Fogra has not certified, approved or endorsed it.

## ICC sRGB profiles (test data only)

`tests/testdata/icc/sRGB_v4_ICC_preference.icc` and `tests/testdata/icc/sRGB2014.icc` are published by the International
Color Consortium (https://registry.color.org/rgb-registry/srgbprofiles) under its general licensing terms for ICC profiles:
permission to use, copy and distribute the files for any purpose is granted without fee, provided the files are not changed
(including the ICC copyright notice tag) and the name of the ICC is not used in advertising or publicity. They are provided
"AS IS" with no express or implied warranty. They are used only by the test project and are not part of the shipped package.
