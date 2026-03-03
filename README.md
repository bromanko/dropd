# Drop D

A personal service that continuously creates and updates Apple Music playlists tailored to my tastes, powered by the Apple Music API and other third-party data sources.

## Development Environment

This project uses a [Nix flake](https://nix.dev/concepts/flakes) to provide a reproducible development shell with all required tools (`.NET 9 SDK`, `gnumake`, `overmind`, `tmux`).

### With direnv (recommended)

If you have [direnv](https://direnv.net/) installed, the shell activates automatically when you enter the project directory:

```sh
direnv allow
```

### Without direnv

Run any command inside the Nix shell directly:

```sh
nix develop -c <command>
```

For example:

```sh
nix develop -c dotnet --version
nix develop -c make spike
```
