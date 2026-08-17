namespace Jondo.Unity.Uplauncher
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            try
            {
                buttonPlay.Enabled = false;

                labelStatus.Text =
                    "Lecture de la configuration...";

                LauncherConfig config =
                    DofusLauncher.LoadConfig();

                labelStatus.Text =
                    "Lancement de Dofus...";

                DofusLauncher.StartDofus(
                    config
                );

                labelStatus.Text =
                    "Dofus lancé.";
            }
            catch (Exception ex)
            {
                labelStatus.Text =
                    "Erreur";

                MessageBox.Show(
                    ex.ToString(),
                    "Jondo Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                buttonPlay.Enabled = true;
            }

        }

        private void label1_Click(object sender, EventArgs e)
        {

        }
    }
}
