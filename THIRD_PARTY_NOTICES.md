# Third-party software

TakeoutDateRepair invokes ExifTool as a separate process to read and write metadata.

## ExifTool 13.59

- Author: Phil Harvey.
- Project: https://exiftool.org/
- Source and documentation: https://github.com/exiftool/exiftool
- License: ExifTool is distributed under the same terms as Perl (Artistic License or GPL).
- The unmodified Windows distribution contains the ExifTool Perl source, documentation, license files and additional components.

## Windows distribution

- Maintainer: Oliver Betz.
- Project and download: https://oliverbetz.de/pages/Artikel/ExifTool-for-Windows
- Distribution used: `exiftool-13.59_64.zip`.
- Archive SHA-256: `577257DD22BAEBE77157D905792D6ED2C5916CD03AA627F40B2175DB12110AC6`.
- Its included GPL v3 text is reproduced at `licenses/ExifTool-GPL-3.0.txt`.
- The distribution's Perl/dependency license collection is reproduced without changes at `licenses/Licenses_Strawberry_Perl.zip`.
- The original archive is embedded without modification in the portable application. At runtime the entire distribution, including its source and licensing documents, is extracted to the application's temporary tools directory.

The MIT license at the repository root does not replace or relicense any third-party component. Upstream source, notices and applicable licenses remain available through the original distribution and the links above.
