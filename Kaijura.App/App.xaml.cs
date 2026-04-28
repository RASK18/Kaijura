using System.Windows;
using Velopack;

namespace Kaijura.App;

public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.Run(new MainWindow());
    }
}
