# Project instructions

Read README.md and INSTALL_FOR_AI.md before installation. Read skills/pvz-mod-player/SKILL.md before live gameplay.

Use focused verification for changed behavior. Documentation-only changes need direct review. Player installation must not require a compiler, Node.js, a model API key, or installing dependencies into system Python. Python tools use only the standard library.

Never commit game-sandbox/, artifacts/, research/, generated/, runtime/, local configuration, saves, connection tokens, or game assemblies. packages/ contains only our compiled bridge and its build metadata. Preserve the release packager's public-file allowlist.

Keep loopback authentication, exact target checks, exclusive controller ownership, player-stop lock, unknown-action non-replay, persistent purchase receipts, and verified release. Do not hot-replace a live Mod or start a second controller. Offline editing does not justify stopping a healthy runner.
