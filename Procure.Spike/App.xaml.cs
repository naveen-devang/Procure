using System;
using Microsoft.UI.Xaml;

namespace Procure.Spike;

public partial class App : Application
{
    public static string DbDir { get; private set; } = "";

    public App()
    {
        InitializeComponent();

        // The spike runs against the real 20k SQLite file. Point at it here if the
        // env var is not already set (set by launch scripts / PowerShell).
        DbDir = Environment.GetEnvironmentVariable("PROCURE_DB_DIR")
                ?? @"E:\Procure\Procure\TestData\procure-20k";
        Environment.SetEnvironmentVariable("PROCURE_DB_DIR", DbDir);

        SQLitePCL.Batteries_V2.Init();
    }

    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
