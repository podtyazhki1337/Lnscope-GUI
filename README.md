# LNscope GUI

Windows software for microscope-assisted cell picking with a Luigs & Neumann stage and micromanipulator. The camera workspace supports live imaging, recording, and experimental cell and spheroid segmentation and tracking.

## Run on Windows x64

Download the ZIP from [GitHub Releases](https://github.com/podtyazhki1337/Lnscope-GUI/releases/latest), extract the entire folder, and launch `GUI.exe`. The package includes .NET, Python, and FFmpeg. The default configuration uses simulated devices. For a physical controller or camera, follow the [hardware setup guide](docs/HARDWARE_SETUP.md).

## Record an experiment

Connect the positioners and camera, open the live view, and select **Record**. Use the **Exposure** and **Color** tabs in the right sidebar below Recording to adjust supported camera settings. Each session creates a timestamped folder containing an MP4 and a matching CSV. The first CSV row gives the initial axis coordinates and configured ports. Each video frame has a capture timestamp and a full coordinate snapshot. Additional rows preserve observed movement between frames. [CSV format and video analysis](docs/VIDEO_ANALYSIS_AND_LOGGING.md)

## Develop

The interface is written in C# (WPF). Motion control and image analysis use an [editable Python backend](GUI/PythonBackend/README.md). With the .NET 10 SDK installed, run these commands from the repository root:

```powershell
./run.ps1         # Start the application
./run.ps1 -Check  # Run simulator and recording checks
./publish.ps1     # Build the portable Windows x64 folder
```
