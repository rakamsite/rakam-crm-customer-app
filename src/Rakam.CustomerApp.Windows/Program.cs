namespace Rakam.CustomerApp.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, "Local\\RakamCustomerApp-" + Environment.UserName, out var first);
        if (!first) { MessageBox.Show("برنامه از قبل باز است.", "راکام"); return; }
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MainWindow()); }
        catch (Exception) { MessageBox.Show("برنامه باز نشد. وجود WebView2 Runtime و دسترسی به پوشه اطلاعات را بررسی کنید.", "راکام"); }
    }
}
