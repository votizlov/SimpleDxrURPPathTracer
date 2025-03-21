# DXR PathTracer in Unity
![Alt text](screenshots/Screenshot1.png?raw=true "Preview 1")
![Alt text](screenshots/Screenshot2.png?raw=true "Preview 2")

Simple PathTracer implemented in Unity and powered by DXR. You can see it as an example how to use DXR API/shaders in Unity.
* Dead simple. Only few hundred lines of code. No Monte Carlo integration, no probability distribution functions, no importance sampling, no denoising. It should be easy to follow (assuming you know what a path-tracer is)
* Implemented for default unity renderer
* Four simple material types (diffuse, metal, glass and emissive material)
* No analytical light sources, only emissive materials and a simple "emissive" background
* Not physically accurate
* Very simple, very slow  
Ported from https://github.com/SlightlyMad/SimpleDxrPathTracer

## Requirements
* Unity 2019.3b2 or newer
* Created with URP 14.0.11
* DXR compatible graphics card (NVidia RTX series or some cars from NVidia GeForce 10 and 16 series)
* Windows 10 (v1809 or newer)
