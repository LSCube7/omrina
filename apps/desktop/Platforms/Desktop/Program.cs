using Uno.UI.Hosting;

var host = UnoPlatformHostBuilder.Create()
    .App(() => new Omrina.Desktop.App())
    .UseX11()
    .UseLinuxFrameBuffer()
    .UseMacOS()
    .UseWin32()
    .Build();

host.Run();
