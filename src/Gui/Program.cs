using System;
using System.Windows.Forms;
namespace SwitchPorter.Gui;
static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
