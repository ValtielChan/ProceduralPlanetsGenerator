namespace Valtiel.PlanetGenerator.Generation
{
    // Which path to use when (re)generating a planet's cubemaps.
    //
    // GPU — dispatches the compute shaders. Fast (1–5 ms per cubemap at 512²)
    //       but requires a GPU + driver with compute support: DX11+, Vulkan,
    //       Metal, or WebGPU. Default WebGL 2.0 does NOT support compute.
    //
    // CPU — runs a Burst-compiled Job per pixel. Slower (50–300 ms at 512²
    //       depending on the mode + core count) but works on every platform
    //       Unity targets, including WebGL 2.0 builds.
    public enum GenerationBackend
    {
        GPU,
        CPU,
    }
}
