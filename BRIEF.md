A cross-platform video editor using .NET 10 and C# that runs on Windows 11 and Linux.
Features:
- multiple video tracks
- multiple audio tracks
- non-destructive editing
- robust undo/redo — this is an important, first-class requirement, not an afterthought
- proxy media: generate and edit against lower-resolution proxies for faster preview/render
  performance, while exporting from the full-resolution originals
- uses hardware acceleration
- multithreaded
- effects like brightness, color, contrast
- fades
- volume level mixing
- supports plugins eventually
- can leverage OSS wherever possible especially around handling video formats or advanced things like color grading

See [UI.md](UI.md) for the target UI and the features its mockup implies.