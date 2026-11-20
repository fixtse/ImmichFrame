[![Contributors][contributors-shield]][contributors-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![MIT License][license-shield]][license-url]

<!-- PROJECT LOGO -->
<br />
<div align="center">
  <a href="https://github.com/3rob3/ImmichFrame">
    <img src="design/AppIcon.png" alt="Logo" width="200" height="200">
  </a>

  <h3 align="center">ImmichFrame</h3>

  <p align="center">
    An awesome way to display your photos as a digital photo frame
    <br />
    <a href="https://immich.app/"><strong>Explore immich »</strong></a>
    <br />
    <br />
    <a href="https://immichframe.dev">Documentation</a>
    ·
    <a href="https://demo.immichframe.dev">Demo</a>
    ·
    <a href="https://github.com/3rob3/ImmichFrame/issues">Report Bug</a>
    ·
    <a href="https://github.com/3rob3/ImmichFrame/issues">Request Feature</a>
  </p>
</div>

## 📄 Documentation
You can find the documentation [here](https://immichframe.dev).

## 🖼️ Demo

You can find a working demo [here](https://demo.immichframe.dev).

<img src="/design/demo/web_demo.png" alt="Web Demo">

## ⚠️ Disclaimer

**This project is not affiliated with [immich][immich-github-url]!**

## 📜 License

[GNU General Public License v3.0](LICENSE.txt)

## 🆘 Help

[Discord Channel][support-url]

## 🙏 Acknowledgments

- BIG thanks to the [immich team][immich-github-url] for creating an awesome tool

## 🌟 Star History

[![Star History Chart](https://star-history.dera.page/svg?repos=immichframe/immichframe&type=Date)](https://star-history.dera.page/#immichframe/immichframe&Date)

<!-- MARKDOWN LINKS & IMAGES -->
<!-- https://www.markdownguide.org/basic-syntax/#reference-style-links -->

[contributors-shield]: https://img.shields.io/github/contributors/immichFrame/ImmichFrame.svg?style=for-the-badge
[contributors-url]: https://github.com/immichFrame/ImmichFrame/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/immichFrame/ImmichFrame.svg?style=for-the-badge
[forks-url]: https://github.com/immichFrame/ImmichFrame/network/members
[stars-shield]: https://img.shields.io/github/stars/immichFrame/ImmichFrame.svg?style=for-the-badge
[stars-url]: https://github.com/immichFrame/ImmichFrame/stargazers
[issues-shield]: https://img.shields.io/github/issues/immichFrame/ImmichFrame.svg?style=for-the-badge
[issues-url]: https://github.com/immichFrame/ImmichFrame/issues
[license-shield]: https://img.shields.io/github/license/immichFrame/ImmichFrame.svg?style=for-the-badge
[license-url]: https://github.com/immichFrame/ImmichFrame/blob/master/LICENSE.txt
[releases-url]: https://github.com/immichFrame/ImmichFrame/releases/latest
[support-url]: https://discord.com/channels/979116623879368755/1217843270244372480
[openweathermap-url]: https://openweathermap.org/
[immich-github-url]: https://github.com/immich-app/immich
[immich-api-url]: https://immich.app/docs/features/command-line-interface#obtain-the-api-key

## OpenNextion Room Companion

On Room Companion firmware with the ImmichFrame idle mode, enter the
ImmichFrame base URL in the panel's **ImmichFrame server** entity, choose
**ImmichFrame** as the **Idle screen**, and set the **Slideshow interval**.
The panel fetches one JPEG per interval using the selected display orientation.
No Remote WebView server is required.

The native JPEG endpoint is `GET /api/Asset/OpenNextion`. It picks photos
from the same pool as the web client, so the configured accounts, albums,
excluded albums, people, date and rating filters all apply (videos are
skipped). The `back=true` history and a held-over pairing photo are cleared
when account settings change. It uses the configured ImmichFrame accounts and filters and the same authentication as
other asset endpoints. If `AuthenticationSecret` is configured, send it in
the `Authorization: Bearer …` request header. Use HTTPS with authentication.

| Panel | Portrait | Landscape |
| --- | --- | --- |
| ONX3248G035 (3.5-inch) | `width=320&height=480` | `width=480&height=320` |
| ONX2432G028 (2.8-inch) | `width=240&height=320` | `width=320&height=240` |

For example, `/api/Asset/OpenNextion?width=480&height=320` returns a
landscape JPEG. The default fit is `contain`, which preserves the full
photo and fills any margins with black. Add `fit=cover` to fill the output
by cropping around the centre, or `fit=blur` to keep the whole photo over a
blurred, darkened copy of itself.

Add `split=true` for ImmichFrame's split view: when a photo's orientation
does not suit the screen (a portrait photo on a landscape screen, or the
reverse), a second photo of the same orientation is shown beside or below
it. Orientation comes from Immich's metadata; a candidate that does not
match is shown on the next request instead.

Add `info=date,location,album` (any of `date`, `location`, `album`,
`description` and `people`) to choose the photo details drawn in the
top-left corner, or `info=true` to follow
the `ShowPhotoDate`, `ShowImageLocation`, `ShowAlbumName`, `ShowImageDesc`
and `ShowPeopleDesc` settings with `PhotoDateFormat`, `Language` and
`ImageLocationFormat`. At most three lines are drawn. The panel keeps the
bottom of the screen for its own clock and weather. Add `back=true` to return to the
previously shown screen (the last 20 are kept); the next request without it
steps forward again before new photos are picked. The standard ImmichFrame web app remains
available for videos and browser controls.

This fork publishes containers to `ghcr.io/fixtse/immichframe`. Pushes to
`main` update `latest`, and version tags such as `v1.0.40.1` publish the
matching image tag. Each build also publishes a `sha-<commit>` tag for
pinning deployments.

### Discovery

The server announces itself as `_immichframe._tcp` over mDNS, so the panel's
web setup (ImmichFrame page) lists it like Home Assistant's discovery. Run the
container with `network_mode: host` so the announcement reaches the network.
Optional environment variables: `OPENNEXTION_MDNS=false` turns it off,
`OPENNEXTION_MDNS_NAME` sets the shown name, `OPENNEXTION_URL` sets the address
panels should use (for example behind a reverse proxy) and
`OPENNEXTION_MDNS_PORT` sets the port when it differs from
`ASPNETCORE_HTTP_PORTS`/`ASPNETCORE_URLS` (default 8080).
