# Bundled CPython

CPython 3.13.15, official Windows x64 embeddable distribution.

Source and release information: https://www.python.org/downloads/release/python-31315/

Archive: https://www.python.org/ftp/python/3.13.15/python-3.13.15-embed-amd64.zip

SHA-256 (verified before extraction):
`d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf`

The CPython license is in `LICENSE.txt` in this directory. The application includes
`Lib/site-packages` on the isolated runtime path for OpenCV, NumPy and ONNX Runtime.
The motion backend uses the standard library. Video analysis loads its dependencies
from the distribution without installing packages or downloading models at startup.
The host uses an absolute runtime path, isolated mode (`-I`) and UTF-8 mode.
