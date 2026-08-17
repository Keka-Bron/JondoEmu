using System;
using System.Collections.Generic;
using System.Text;

namespace Jondo.Unity.Uplauncher
{
    internal class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
