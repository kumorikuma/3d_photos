# Generating 3D Photos in Unity

Demonstration of how to make a simple 3D Photo for https://kumorikuma.dev/3d_photos/. 

> Note: Requires Git LFS to checkout completely.

![Screenshot of Unity Scene](https://github.com/kumorikuma/3d_photos/blob/main/Assets/Diagrams/unity_screenshot.jpg)

Options are accessed through UnityEditor Windows in the toolbar:
- **Custom -> Mesh Generation**
  - Main menu for creating 3D Photos. Most algorithm options here are for debugging and visualization purposes and can be left alone.
- **Custom -> Mesh Editing**
  - Currently only for adding blendshapes to other meshes
- **Custom -> Scene Camera Controls**
  - Edit Mode animation controller for generating visualizations

Usage:
1. Obtain an image, a depth map of the image, and a separate foreground RGBA image with the background transparent. Determine the FOV that the photo was taken with. For details, checkout the article: https://kumorikuma.dev/3d_photos/
2. Set the images and FOV as input parameters, then hit **Generate 3D Photo**.
