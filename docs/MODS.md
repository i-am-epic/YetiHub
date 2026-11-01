# Writing a YetiHub mod

A mod is a .NET class library that references `YetiHub.Mods.Abstractions` and implements `IYetiMod`.

## 1. Create the project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Don't ship the API assembly with your mod: YetiHub provides it. -->
    <ProjectReference Include="path\to\YetiHub.Mods.Abstractions.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

## 2. Implement `IYetiMod`

```csharp
public sealed class AutoGainMod : IYetiMod
{
    public string Id => "me.autogain";          // unique, stable, lowercase
    public string Name => "Auto Gain";
    public string Description => "Lowers gain when you clip.";
    public Version Version => new(1, 0, 0);

    public void OnLoad(IModContext ctx)
    {
        ctx.MicChanged += (_, mic) => ctx.Log(mic is null ? "Yeti unplugged" : $"Yeti at {mic.Gain:P0}");
    }

    public void OnUnload() { }
}
```

What a mod can do through `IModContext`:

| Member | Use |
|---|---|
| `Mic` / `MicChanged` | Read and set gain and mute; read the live peak level. |
| `RegisterProcessor` | Add an `IAudioProcessor` to the live audio chain (DSP: gates, EQ, compressors). |
| `DataDirectory` | Your own folder for settings and data. |
| `Log` | Write to YetiHub's Activity panel. |

`IAudioProcessor.Process` runs on the real-time audio thread. Don't block, allocate or throw in it.
YetiHub disables a processor that throws.

## 2b. Show up in the character gallery (API 1.1+)

Implement `IProcessorInfo` on your processor to get a card with an emoji and description. Processors whose `Group` is
`IProcessorInfo.CharacterGroup` appear in the **Character voices** gallery, where only one is active at a time:

```csharp
public sealed class Pirate : IAudioProcessor, IProcessorInfo
{
    public string Name => "Pirate";
    public string Emoji => "🏴‍☠️";
    public string Description => "Arr.";
    public string Group => IProcessorInfo.CharacterGroup;
    public bool Enabled { get; set; }
    public void Process(Span<float> samples, int sampleRate, int channels) { /* … */ }
}
```

See `mods/YetiHub.Mods.CharacterVoices` for 27 complete examples and a small DSP kit (filters, pitch shifter, delays,
reverb, speech detector). YetiHub soft-limits everything after mods, so a mod can't clip the output.

## 3. Install it

Copy the build output into `%APPDATA%\YetiHub\mods\<AssemblyName>\` (use **Open mods folder** in the app).
The DLL name must match the folder name. Restart YetiHub.

## Where processed audio goes

Mod processors run **after** the built-in Voice FX chain, on the same signal that goes to *Hear yourself* and to
the virtual mic, so Discord/OBS hear your mod when a virtual cable is selected. The bundled
`mods/YetiHub.Mods.RobotVoice` is a complete example; processors may start disabled (`Enabled = false`) and the
user switches them on in the Mods tab, which YetiHub remembers.

## API versioning

`YetiHub.Mods.Abstractions` follows semver through its assembly version. YetiHub refuses to load a mod
built against a different **major** version, so breaking API changes must bump the major.
