# Marketplace media

`media/icon.png` is 288×288. The thumbnail and three gallery images are 1920×960 PNGs, with English captions and DeskGlide's existing monogram.

The keys are illustrations of actual action states. Gallery 2 includes the real WPF Stream Deck settings page rendered by [Windows CI](https://github.com/Aleksei-Melnik/DeskGlide/actions/runs/38309237368); it contains fixture data, not a private user profile.

To regenerate on Windows with Python and Pillow:

```powershell
python streamdeck/scripts/marketplace_media.py --settings path/to/Host-10.png
```

Use the packaged `com.yami.deskglide.streamDeckPlugin`, `listing.md`, and these images in Maker Console. The public Maker profile is Yami (@yami). Do not copy personal account details into the public listing.

The first app/plugin integration checks passed against the exact portable EXE and the official Elgato SDK. Physical Stream Deck button presses and a real two-PC user setup remain separate hardware checks.
