namespace TEHhub.OffsetDoctor.Gui;

using System;
<<<<<<< HEAD
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
=======
using System.Threading.Tasks;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            using var app = new OffsetDoctorApp();
            await app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OffsetDoctor.Gui] Fatal error: {ex.Message}");
        }
>>>>>>> 486ddd1db1fda98cedf2d2d86e5f0800e42be271
    }
}
