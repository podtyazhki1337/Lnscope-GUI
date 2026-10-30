# Bundled CPython

CPython 3.13.15, official Windows x64 embeddable distribution.

Source and release information: https://www.python.org/downloads/release/python-31315/

Archive: https://www.python.org/ftp/python/3.13.15/python-3.13.15-embed-amd64.zip

SHA-256 (verified before extraction):
`d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf`

The runtime files are unmodified. License: `LICENSE.txt` in this directory.
The backend uses only the Python standard library. No pip, third-party packages,
global installation, registry changes or downloads at application startup.
The host uses an absolute runtime path, isolated mode (`-I`) and UTF-8 mode.
