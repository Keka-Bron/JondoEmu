using System.Windows.Forms;
using Jondo.Unity.Deobfuscator;
using Jondo.Unity.Deobfuscator.UI;

// ─── The deobfuscator ───────────────────────────────────────────────────────────────────
//
// The face of what until now were nine commands in an order one had to know by heart. It guides step by
// step: it asks for the new client, asks for the version already known, reads the game's code, matches
// the two, asks a model about whatever remains in doubt and lets one review proposal by proposal.
//
// The command line still exists and does exactly the same calling the same classes of
// Jondo.Unity.Reversing: whoever prefers a script loses nothing, and what is measured one way holds
// the other.
//
// Unlike the server and the launcher, here there is nothing else to attend to: there is no need to open
// the window on a separate thread, the main thread is the interface's and that is it.

ApplicationConfiguration.Initialize();
Application.SetHighDpiMode(HighDpiMode.SystemAware);

var settings = Settings.Load();
Application.Run(new MapperWindow(settings));
