# Maintainer: ColdLemonTea <me@lemonice.top>

pkgname=thrm-bin
pkgver=3.6.3
pkgrel=1
pkgdesc='Flydigi BS-series laptop cooler controller (prebuilt)'
arch=('x86_64')
url='https://github.com/ColdLemonTea/THRM'
license=('MIT')
depends=(
  'gdk-pixbuf2'
  'glib2'
  'glibc'
  'gtk3'
  'libpulse'
  'libx11'
  'systemd-libs'
)
optdepends=('bluez: BS1 BLE support')
provides=("thrm=${pkgver}")
conflicts=('thrm')
options=('!strip' '!debug')

# This PKGBUILD packages the binaries already produced by build.sh/CI. makepkg
# imports every payload into $srcdir before package() runs, including in a
# clean chroot.
source=(
  "thrm-flutter-linux-bundle.tar.gz::file://${startdir}/build/thrm-flutter-linux-bundle.tar.gz"
  "thrm-core::file://${startdir}/build/thrm-core"
  "99-flydigi-fan.rules::file://${startdir}/scripts/99-flydigi-fan.rules"
  "thrm.desktop::file://${startdir}/packaging/linux/thrm.desktop"
  "thrm.png::file://${startdir}/ui/assets/brand/appicon.png"
  'LICENSE'
)
sha256sums=('SKIP' 'SKIP' 'SKIP' 'SKIP' 'SKIP' 'SKIP')

package() {
  install -d "${pkgdir}/usr/lib/thrm" "${pkgdir}/usr/bin"
  cp -a "${srcdir}/bundle/." "${pkgdir}/usr/lib/thrm/"
  install -Dm755 "${srcdir}/thrm-core" "${pkgdir}/usr/lib/thrm/thrm-core"
  ln -s ../lib/thrm/thrm "${pkgdir}/usr/bin/thrm"
  ln -s ../lib/thrm/thrm-core "${pkgdir}/usr/bin/thrm-core"
  install -Dm644 "${srcdir}/99-flydigi-fan.rules" \
    "${pkgdir}/usr/lib/udev/rules.d/99-flydigi-fan.rules"
  install -Dm644 "${srcdir}/thrm.desktop" \
    "${pkgdir}/usr/share/applications/com.tianli0.thrm_ui.desktop"
  install -Dm644 "${srcdir}/thrm.png" \
    "${pkgdir}/usr/share/icons/hicolor/256x256/apps/com.tianli0.thrm_ui.png"
  install -Dm644 "${srcdir}/LICENSE" \
    "${pkgdir}/usr/share/licenses/${pkgname}/LICENSE"
}
