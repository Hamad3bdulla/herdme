# HerdMe — brag plan

**Tone:** "fake Series A launch from 2016" → yc-parody pacing (one claim per scene, hard-ish cuts), dressed in 2016 startup-keynote style: huge geometric type (Poppins), flat colour blocks, long-shadow flat icons, bouncy easing, ukulele + handclaps + glockenspiel soundtrack. Played completely straight.

**What it is:** HerdMe is a free, open-source local PHP & Laravel environment — native on Windows (WinUI 3) and macOS.
**Who for:** Laravel/PHP devs who want `project.test` over HTTPS, PHP 8.0–8.5, databases and tools, without subscriptions or license keys.
**Sets it apart:** no subscriptions, no activation, no license keys (README); MIT; English + Arabic RTL.
**Funniest true claim:** a 2016 startup launch whose business model is… there isn't one. No subscriptions, no license keys.
**Visual hook:** keynote card "We're disrupting localhost." typed in with a red cursor.
**Real UI:** the redesigned Windows shell (Mica title bar, "Local sites: Running" badge, NavigationView), Sites page with *Create Laravel*, the Dashboard "Everything is ready" card, flipped to Arabic RTL. Rebuilt from `Styles/DesignSystem.xaml` colours (#C92B3D brand, #F4F5F7 shell, tints #FCEDEF/#E7F5F0/#EAF1FC/#FFF3DB) and strings from `Strings/en-US` + `Strings/ar` Resources.resw. Real app icon from `AppIcon.appiconset`.
**Share caption:** "We're disrupting localhost. HerdMe: .test domains, HTTPS, PHP 8.0–8.5 and your databases on Windows — no subscriptions, no license keys."

## Storyboard (1920×1080, 30fps, 22.5s = 42 beats at 112 bpm)

| # | Time | Scene | On screen |
|---|---|---|---|
| 1 Hook | 0.0–2.7 | Keynote card, white | "We're disrupting" types in, then "localhost." slams in red. |
| 2 Reveal | 2.7–5.9 | Charcoal block wipes up; icon bounces in with long shadow | "HerdMe" + "Local PHP & Laravel. Native on Windows." |
| 3 Highlight — sites | 5.9–10.7 | Windows app window slides up | Sites page; cursor clicks **Create Laravel**; `acme.test` row appears, dot turns green, browser pill types `https://acme.test` with lock. Caption: "One click. Real HTTPS. .test domains." |
| 4 Highlight — stack | 10.7–14.5 | Flat tiles cascade | PHP 8.0 → 8.5 chips, then MariaDB, MySQL, PostgreSQL, MongoDB, Redis, Meilisearch, MinIO, RustFS. Caption: "Your whole stack. Downloaded and verified." |
| 5 Highlight — RTL | 14.5–17.7 | Dashboard card, mirror-flip | "Everything is ready" → "كل شيء جاهز" with the layout flipping right-to-left. Caption: "English. العربية." |
| 6 Punchline | 17.7–22.5 | Keynote card again | "Our business model:" → "There isn't one." → outro: icon, HerdMe, "No subscriptions · No license keys · MIT", github.com/Hamad3bdulla/herdme |

## Sound
C major, 112 bpm. Plucked ukulele chords (Karplus-Strong) C–G–Am–F, handclaps on 2 & 4, glockenspiel melody, kick soft. SFX in-key: glock ping for the cursor click, soft tick for typing, a low "thump" (C2) for the "localhost." slam, glock arpeggio for the chip cascade, all mixed under the music. Music ducks for the punchline and resolves on a final C chord.

**Poster / frame 0:** settled Sites frame at t=10.5s (acme.test Running, `https://acme.test` Secure).
