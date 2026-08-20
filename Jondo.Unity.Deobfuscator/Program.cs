using System.Windows.Forms;
using Jondo.Unity.Deobfuscator;
using Jondo.Unity.Deobfuscator.UI;

// ─── El desofuscador ────────────────────────────────────────────────────────────────────
//
// La cara de lo que hasta ahora eran nueve comandos en un orden que había que saberse. Lleva paso a
// paso: pide el cliente nuevo, pide la versión que ya se conocía, lee el código del juego, empareja
// las dos, pregunta a un modelo por lo que quede en duda y deja revisar propuesta a propuesta.
//
// La línea de comandos sigue existiendo y hace exactamente lo mismo llamando a las mismas clases de
// Jondo.Unity.Reversing: quien prefiera un guión no pierde nada, y lo que se mida por un lado vale
// por el otro.
//
// A diferencia del servidor y del lanzador, aquí no hay nada más que atender: no hace falta abrir
// la ventana en un hilo aparte, el hilo principal es el de la interfaz y punto.

ApplicationConfiguration.Initialize();
Application.SetHighDpiMode(HighDpiMode.SystemAware);

var settings = Settings.Load();
Application.Run(new MapperWindow(settings));
