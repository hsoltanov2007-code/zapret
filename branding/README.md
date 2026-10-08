# Northpass route mark

The original Northpass mark is a simple northbound route with an intentional gateway gap. It uses one continuous rounded route and a short terminal stroke; it works in either light or dark monochrome. The SVGs are the editable master assets. The application ICO contains 16, 24, 32, 48, 64, 128 and 256-pixel images and is used by the executable, tray, window and installer. The WPF title/About drawing uses the identical path geometry.

Re-render the original vector asset (ImageMagick 7):

```sh
magick -size 256x256 xc:none -draw 'scale 4,4 fill "#17171B" stroke none roundrectangle 1,1 63,63 15,15 fill none stroke "#EEEEF2" stroke-width 7 stroke-linecap round stroke-linejoin round path "M18,18 V46 L46,28 V46 M46,14 V18"' -define icon:auto-resize=256,128,64,48,32,24,16 src/Northpass.App/Assets/Northpass.ico
```

Do not put component/project logos on the main product surface. Third-party attribution remains in Licenses & legal.
