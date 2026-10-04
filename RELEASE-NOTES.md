ScreenCapture 0.7.3 adds idle-sleep protection, enabled by default on every PC.

- Settings → Передача экрана → При бездействии → Не усыплять ПК и не выключать экран.
- While ScreenCapture runs, Windows is asked to keep both the computer and display awake, independently of NDI, replay and KVM activity.
- Disabling the setting or exiting releases the request. Windows power-plan settings are not modified; manually selected sleep remains available.
- Existing settings automatically receive the enabled default. Explicitly disabling it is preserved across restarts and profile backup/restore.

Validation: Windows execution-state flags confirmed both system/display requests and their release; settings migration, copy/JSON persistence, UI, existing feature and update tests passed.
