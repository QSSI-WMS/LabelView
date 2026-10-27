namespace LabelView
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            string? initialFile = args.Length > 0 ? args[0] : null;
            Application.Run(new Form1(initialFile));
        }
    }
}
