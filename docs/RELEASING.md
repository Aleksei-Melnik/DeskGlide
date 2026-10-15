# Signed releases

Source code and release downloads are public. Private signing keys and tokens must never be committed or attached to releases.

The RSA-4096 update key introduced in 0.7.26 has public-key SHA-256 `8872383350775B7EBEF0F1841FCD00726DCF22F9CE225D51248D8E9B42579B43`. The matching private key is held in the repository's encrypted Actions secret `DESKGLIDE_RELEASE_PRIVATE_KEY`. An additional local copy is stored outside the checkout with access restricted to the Windows user and SYSTEM. GitHub secrets cannot be downloaded back as plaintext; the release workflow can keep signing after a local reinstall.

For subsequent versions, update the project version and release notes, push to `main`, then run **Publish signed DeskGlide release** from Actions with that version. The workflow runs the full validation suite before signing and publishing. Pull requests do not receive the signing secret. The release workflow accepts `main`, uses pinned action commits and removes its temporary key in a `finally` block. The publisher verifies the manifest signature and archive/EXE hashes and targets an exact source commit. Uploads are staged as a draft before publication.

Local fallback: `scripts/New-Release.ps1 -SigningKey <private-key-path-outside-repo>`, then `scripts/Publish-Release.ps1 -ReleaseFolder <folder> -NotesFile <current-release-notes>`.

Do not rotate this key as part of a normal release: older installations trust its matching public key. The explicitly authorized 0.7.26 rotation requires one manual EXE replacement from older versions; future updates use the new signed channel.
