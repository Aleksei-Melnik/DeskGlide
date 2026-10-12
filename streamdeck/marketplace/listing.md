# Elgato Marketplace submission

Name: DeskGlide

Author / Maker organization: Yami (@yami; created and verified in Maker Console)

Price: Free

Plugin UUID: com.yami.deskglide

Version: 1.0.0.0

## Description

Control DeskGlide from Stream Deck: save instant replay clips, open a remote PC or server, and lock mouse transitions between screens. Live buttons show your replay buffer, connected computers and successful clip delivery. No simulated keyboard shortcuts are required.

Use Save Replay, Open KVM, Lock Mouse Transitions, Toggle Replay and Toggle Streaming. Choose a connected computer for direct KVM access, or open the computer chooser. With a Stream Deck on your receiving PC, paired-controller actions can save replay and control streaming on your gaming PC through DeskGlide's existing authenticated connection.

Requires Windows 10/11, Stream Deck 7.1 or later, and DeskGlide 0.7.19 or later running in the same Windows user session. Both DeskGlide and this plugin are free. Actions and setup support English and Russian.

## Additional links

- Download DeskGlide: https://github.com/Aleksei-Melnik/DeskGlide/releases/latest
- Setup guide: https://github.com/Aleksei-Melnik/DeskGlide/blob/main/docs/STREAM-DECK.md
- Support: https://github.com/Aleksei-Melnik/DeskGlide/issues
- Privacy: https://github.com/Aleksei-Melnik/DeskGlide/blob/main/docs/STREAM-DECK-PRIVACY.md
- Source: https://github.com/Aleksei-Melnik/DeskGlide/tree/main/streamdeck

## Release notes

First release. Five actions, configurable local/paired controller target, direct KVM computer selection, live buffer/status feedback, automatic reconnection, and English/Russian settings. Uses the official Elgato Node SDK. No automatic replay retries after disconnect.

## AI disclosure for review

AI tools assisted with implementation, tests, documentation and supporting media. Yami defined the product behavior, UI preferences, hardware workflow and requirements, and provided hands-on testing and bug reports for DeskGlide. Do not omit the AI declaration if the submission form requests it. Elgato decides eligibility and approval; this document does not establish acceptance.

## Reviewer test notes

1. Download and start DeskGlide 0.7.19 from the supplied release link.
2. Add Save Replay to Stream Deck. Enable replay in DeskGlide and wait for footage.
3. Press Save: Saving/Queued/Saved represent export, delivery and actual delivery completion.
4. Toggle recording and streaming. Set up a second paired DeskGlide PC to check KVM and paired-controller actions.
5. Exit/restart DeskGlide: actions show Offline, then recover. Previously sent commands are not repeated.
6. Review the source and CI checks. Simulator tests do not claim a physical key press or a real two-PC Stream Deck session was tested.

## Submission requirements checked

- Official CLI validates and packs the installer.
- Plugin icons: PNG 256/512; action/category icons: white SVG; key artwork: SVG.
- Marketplace icon: PNG 288×288.
- Thumbnail + three gallery images: PNG 1920×960.
- All Marketplace captions are English; no claim of Elgato endorsement.
- No private account email/password in the package or repository.
- Account registration and organization creation are complete. The user accepted the Maker Agreement.
- Submitted plugin 1.0.0.0 on October 12, 2026. Maker Console shows Pending review; automatic publication after approval is enabled. This is not a Marketplace approval.
