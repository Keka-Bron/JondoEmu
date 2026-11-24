using System.Collections.Generic;

namespace Jondo.Unity.Launcher.UI.News
{
    internal enum RoadmapState { Done, Now, Next }

    /// <summary>One step of the roadmap: when, what, and a line on it, in the launcher's language.</summary>
    internal sealed record RoadmapStep(RoadmapState State, string When, string Title, string Text);

    /// <summary>
    /// Where the emulator is going, as the News page shows it: what has just landed, what is being
    /// done, and what comes next. Written here, in the launcher's four languages; a release brings
    /// the next version of it.
    /// </summary>
    internal static class Roadmap
    {
        public static IReadOnlyList<RoadmapStep> For(Language language) => language switch
        {
            Language.En => new[]
            {
                new RoadmapStep(RoadmapState.Done, "November 2026", "Seasonal events",
                    "The server window opens and shuts Kwismas Island, Al Howin, Vulkania and the other events: their boats, dungeons and monsters follow."),
                new RoadmapStep(RoadmapState.Now, "Now", "Dungeon review",
                    "Every dungeon played in the real client, boss by boss, from level 200 down."),
                new RoadmapStep(RoadmapState.Next, "January 2027", "Dofus 3.8",
                    "The emulator moves to the 3.8 client, with its new class: the Bellephone."),
            },
            Language.Fr => new[]
            {
                new RoadmapStep(RoadmapState.Done, "Novembre 2026", "Événements saisonniers",
                    "La fenêtre du serveur ouvre et ferme l'île de Nowel, Halouine, Vulkania et les autres événements : leurs bateaux, donjons et monstres suivent."),
                new RoadmapStep(RoadmapState.Now, "En cours", "Révision des donjons",
                    "Chaque donjon joué dans le vrai client, boss après boss, du niveau 200 vers le bas."),
                new RoadmapStep(RoadmapState.Next, "Janvier 2027", "Dofus 3.8",
                    "L'émulateur passe au client 3.8, avec sa nouvelle classe : le Bellephone."),
            },
            Language.Pt => new[]
            {
                new RoadmapStep(RoadmapState.Done, "Novembro 2026", "Eventos sazonais",
                    "A janela do servidor abre e fecha a Ilha de Natal, Halouine, Vulkania e os outros eventos: seus barcos, masmorras e monstros acompanham."),
                new RoadmapStep(RoadmapState.Now, "Agora", "Revisão das masmorras",
                    "Cada masmorra jogada no cliente real, chefe por chefe, do nível 200 para baixo."),
                new RoadmapStep(RoadmapState.Next, "Janeiro 2027", "Dofus 3.8",
                    "O emulador passa para o cliente 3.8, com a sua nova classe: o Bellephone."),
            },
            _ => new[]
            {
                new RoadmapStep(RoadmapState.Done, "Noviembre 2026", "Eventos de temporada",
                    "La ventana del servidor abre y cierra la isla de Nawidad, Halloween, Vulkania y los demás eventos: sus barcos, mazmorras y monstruos van detrás."),
                new RoadmapStep(RoadmapState.Now, "Ahora", "Revisión de mazmorras",
                    "Cada mazmorra jugada en el cliente real, jefe a jefe, del nivel 200 hacia abajo."),
                new RoadmapStep(RoadmapState.Next, "Enero 2027", "Dofus 3.8",
                    "El emulador pasa al cliente 3.8, con su nueva clase: el Bellephone."),
            },
        };
    }
}
