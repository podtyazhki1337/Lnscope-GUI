# Bundled experimental segmentation models

`cell.onnx` and `spheroid.onnx` were exported from the locally trained YOLO26n-seg
checkpoints in `artifacts/vision_training_large_v1` using Ultralytics 8.4.191,
ONNX opset 17, fixed batch 1, 512×512 input, FP32, and no graph simplification.
`manifest.json` records source checkpoint and ONNX SHA-256 hashes.

The Ultralytics license is preserved in `ULTRALYTICS-LICENSE.txt`. ONNX Runtime
1.31.1 is bundled under `ThirdParty/python/Lib/site-packages/onnxruntime`; its MIT
license and third-party notices are retained with the package and distribution
metadata. Training dependencies are not bundled with the application.

These are experimental visual models. Cell training used selected crops;
the separate full-frame bootstrap run is used for annotation work. They do not
provide calibrated 3D coordinates, collision checks, or permission to move hardware.
