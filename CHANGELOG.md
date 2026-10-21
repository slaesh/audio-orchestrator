# Changelog

## [0.4.0](https://github.com/slaesh/audio-orchestrator/compare/v0.3.0...v0.4.0) (2026-10-21)


### Features

* **volume:** selectable cubic and linear volume profiles ([#4](https://github.com/slaesh/audio-orchestrator/issues/4)) ([bf77472](https://github.com/slaesh/audio-orchestrator/commit/bf77472b2c8dab56c0d5307844745409dc6438ac))


### Bug Fixes

* **volume:** preserve room mix volume on assignment and persist individual mute states ([47a29db](https://github.com/slaesh/audio-orchestrator/commit/47a29dbe066d56933541475f762d85e8150d7722))

## [0.3.0](https://github.com/slaesh/audio-orchestrator/compare/v0.2.0...v0.3.0) (2026-10-19)


### Features

* **orchestrator:** add resilient node upgrades, container status tracking, and clean plugin dialog ([3ecef84](https://github.com/slaesh/audio-orchestrator/commit/3ecef84c53e66c9db29b99e2e170d5b07d3300f3))
* **orchestrator:** unify container update flow and render markdown release notes for spotify and radio plugins ([d2c1f49](https://github.com/slaesh/audio-orchestrator/commit/d2c1f49effa4ec93324b7c1075cd32d14df2b1f8))
* **spotify:** add container update detection and selective node upgrade ([8c14a46](https://github.com/slaesh/audio-orchestrator/commit/8c14a46a7bf5c60d34799a73c8e989ccf33a3ffb))
* **ui:** remove redundant MaxVolume badge and reduce release cache to 5m ([742d78e](https://github.com/slaesh/audio-orchestrator/commit/742d78ed186dc938586274ac509e5989bf5f373c))


### Bug Fixes

* **docker:** ensure compose builds local orchestrator image instead of pulling ([0706b5d](https://github.com/slaesh/audio-orchestrator/commit/0706b5da8f0361ace653502c053f10c18b686c7e))
* **spotify:** backfill missing CurrentVersion and add resilient fallback in UI ([8e97594](https://github.com/slaesh/audio-orchestrator/commit/8e975945e38f07e86357133bc527b6faeea626b9))
* **spotify:** safely handle inactive sessions and enforce group volume on connect ([b3b882d](https://github.com/slaesh/audio-orchestrator/commit/b3b882d17c86ee946e74d913847bf1e97024b291))

## [0.2.0](https://github.com/slaesh/audio-orchestrator/compare/v0.1.0...v0.2.0) (2026-10-18)


### Features

* initial release of audio-orchestrator v0.1.0 ([6323ced](https://github.com/slaesh/audio-orchestrator/commit/6323ced30e4fea1757d1a2d0f85f61485a6271fa))


### Performance Improvements

* prevent background services and migrations during build-time openapi generation ([e5d9342](https://github.com/slaesh/audio-orchestrator/commit/e5d9342e3b58e583f45b0161e2e096255051fa7e))
