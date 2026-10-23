# Contributing

Start with [the build guide](docs/build.md) and [architecture](docs/architecture.md). Preserve the shared EditorSession boundary and the normal Open → edit → Preview/Apply/Cancel → Undo/Redo → save/reopen workflow.

Keep changes focused. For a modeling or file change, exercise a representative original fixture, cancellation/failure preservation, Undo/Redo and a saved/reopened output. Use the smallest relevant existing checks; do not turn a feature into a broad benchmark campaign.

Describe the user-visible change, why it is needed, what was checked and any remaining limits. Distinguish real desktop input from isolated WPF/Core checks. Never claim compatibility or accessibility coverage that was not exercised.

Keep build output and local evidence in ignored directories. Do not commit personal models, credentials, signing keys, original Microsoft assets, downloaded toolchains or dependency checkouts. Preserve dependency notices when changing packaging.

New original contributions are provided under this repository's [MIT license](LICENSE). Third-party material must keep its own attribution and compatible licensing.
