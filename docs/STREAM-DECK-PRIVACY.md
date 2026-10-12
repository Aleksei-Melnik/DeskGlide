# DeskGlide Stream Deck plugin privacy

The plugin does not require an account, collect analytics, or upload recordings,
clipboard content, pairing codes or credentials to its publisher.

It reads DeskGlide's status from a private named pipe on the same PC and Windows
user session: machine name, app version, role, buffered time, recording errors,
save progress, streaming/mouse-transition states and connected PC names/identifiers.
Stream Deck stores the chosen target, computer identifier and language in its
normal action settings. Errors may appear in local SDK logs.

Choosing **Paired controller PC** sends an allowlisted action over DeskGlide's
existing authenticated KVM connection. It does not add a public HTTP API or
network listener. Pairing is configured by the user inside DeskGlide.

The setup/download links open GitHub in your browser. GitHub and Elgato apply
their own privacy policies to visits and software distribution. DeskGlide's
existing update/dependency download behavior is independent of this plugin.

To remove plugin settings, delete the actions or uninstall the plugin in Stream
Deck. For support, use [GitHub issues](https://github.com/Aleksei-Melnik/DeskGlide/issues)
without posting pairing codes, private logs or recording files publicly.
