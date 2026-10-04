namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Los rótulos que ha traído la interfaz nueva, en los tres idiomas.
    /// </summary>
    /// <remarks>
    /// Van aparte de <see cref="LauncherTexts"/> a propósito: aquel catálogo lo comparten el
    /// lanzador y el servidor, y meterle textos que sólo usa una pantalla del lanzador lo
    /// engordaría para nadie. El día que alguno haga falta en los dos sitios, se muda.
    ///
    /// Sin diccionarios ni ficheros: son doce cadenas por idioma y un <c>switch</c> se lee de un
    /// vistazo y no puede quedarse a medias en un idioma sin que el compilador lo diga.
    /// </remarks>
    internal static class Textos
    {
        public static string Jugar(Language i) => i switch
        {
            Language.En => "PLAY",
            Language.Fr => "JOUER",
            _ => "JUGAR",
        };

        public static string Cuentas(Language i) => i switch
        {
            Language.En => "ACCOUNTS",
            Language.Fr => "COMPTES",
            _ => "CUENTAS",
        };

        public static string Ajustes(Language i) => i switch
        {
            Language.En => "SETTINGS",
            Language.Fr => "OPTIONS",
            _ => "AJUSTES",
        };

        public static string Idioma(Language i) => i switch
        {
            Language.En => "Language",
            Language.Fr => "Langue",
            _ => "Idioma",
        };

        public static string Musica(Language i) => i switch
        {
            Language.En => "Music",
            Language.Fr => "Musique",
            _ => "Música",
        };

        public static string Cliente(Language i) => i switch
        {
            Language.En => "Dofus client",
            Language.Fr => "Client Dofus",
            _ => "Cliente de Dofus",
        };

        public static string CuentasGuardadas(Language i) => i switch
        {
            Language.En => "Saved accounts",
            Language.Fr => "Comptes enregistrés",
            _ => "Cuentas guardadas",
        };

        /// <summary>Que el idioma manda también sobre el juego, que es lo que no se adivina.</summary>
        public static string PieIdioma(Language i) => i switch
        {
            Language.En => "The game starts in this language too.",
            Language.Fr => "Le jeu démarre aussi dans cette langue.",
            _ => "El juego arranca también en este idioma.",
        };

        public static string PieCliente(Language i) => i switch
        {
            Language.En => "Only needed if the game is not next to the launcher.",
            Language.Fr => "Utile seulement si le jeu n'est pas à côté du lanceur.",
            _ => "Sólo hace falta si el juego no está junto al lanzador.",
        };

        public static string PieCuentasGuardadas(Language i) => i switch
        {
            Language.En => "Removes the accounts ticked on the Play screen. Accounts already in game are kept.",
            Language.Fr => "Retire les comptes cochés dans l'écran Jouer. Ceux déjà en jeu sont conservés.",
            _ => "Quita las cuentas marcadas en la pantalla de jugar. Las que ya están en el juego se quedan.",
        };

        /// <summary>Lo que se enseña cuando todavía no hay ninguna cuenta guardada.</summary>
        public static string EquipoVacio(Language i) => i switch
        {
            Language.En => "No accounts yet. Add one and it will stay here for next time.",
            Language.Fr => "Aucun compte pour l'instant. Ajoutes-en un et il restera ici.",
            _ => "Todavía no hay ninguna cuenta. Añade una y se quedará aquí para la próxima vez.",
        };

        /// <summary>Cuántas de las marcadas están ya jugando, dicho de forma que se entienda.</summary>
        public static string Resumen(Language i, int marcadas, int enJuego) => i switch
        {
            Language.En => $"{marcadas} ticked · {enJuego} already in game",
            Language.Fr => $"{marcadas} cochés · {enJuego} déjà en jeu",
            _ => $"{marcadas} marcada(s) · {enJuego} ya en el juego",
        };

        public static string Nivel(Language i) => i switch
        {
            Language.En => "Level",
            Language.Fr => "Niveau",
            _ => "Nivel",
        };

        /// <summary>El botón de la música dice lo que HACE, no cómo está.</summary>
        public static string ApagarMusica(Language i) => i switch
        {
            Language.En => "Turn music off",
            Language.Fr => "Couper la musique",
            _ => "Apagar la música",
        };

        public static string EncenderMusica(Language i) => i switch
        {
            Language.En => "Turn music on",
            Language.Fr => "Activer la musique",
            _ => "Encender la música",
        };

        // ─── Graphics: the HD and 4K scenery packs ─────────────────────────────

        public static string Graficos(Language i) => i switch
        {
            Language.En => "Graphics",
            Language.Fr => "Graphismes",
            _ => "Gráficos",
        };

        /// <summary>Where the pack is chosen, which is the part nobody guesses.</summary>
        public static string PieGraficos(Language i) => i switch
        {
            Language.En => "Downloaded from Ankama's servers for your client's version. Choose the pack in the game's " +
                           "options, under “(Experimental) Texture packs for scenery”; it applies on the next map change.",
            Language.Fr => "Téléchargés depuis les serveurs d'Ankama pour la version de ton client. Choisis le pack dans " +
                           "les options du jeu, « (Expérimental) Packs de textures pour les décors » ; il s'applique " +
                           "au prochain changement de carte.",
            _ => "Se descargan de los servidores de Ankama para la versión de tu cliente. El pack se elige en las " +
                 "opciones del juego, en «(Experimental) Packs de texturas para los entornos», y se aplica en el " +
                 "próximo cambio de mapa.",
        };

        public static string NombreDePack(Language i, Packs.TexturePack pack) => pack == Packs.TexturePack.Hd
            ? i switch
            {
                Language.En => "HD pack",
                _ => "Pack HD",
            }
            : i switch
            {
                Language.En => "4K pack",
                _ => "Pack 4K",
            };

        /// <summary>What the client itself says about each pack, shortened.</summary>
        public static string DescripcionDePack(Language i, Packs.TexturePack pack) => pack == Packs.TexturePack.Hd
            ? i switch
            {
                Language.En => "For mid-range configurations and resolutions up to 1920 × 1080.",
                Language.Fr => "Pour les configurations milieu de gamme et les résolutions jusqu'à 1920 × 1080.",
                _ => "Para configuraciones de gama media y resoluciones de hasta 1920 × 1080.",
            }
            : i switch
            {
                Language.En => "Only for recent configurations above 1920 × 1080. Not recommended for multi-accounting.",
                Language.Fr => "Uniquement pour les configurations récentes au-delà de 1920 × 1080. Vivement déconseillé en multi-compte.",
                _ => "Solo para configuraciones recientes por encima de 1920 × 1080. Nada recomendado en multicuenta.",
            };

        public static string EstadoDePack(Language i, Packs.PackStatus s) => s.State switch
        {
            Packs.PackState.NoClient => i switch
            {
                Language.En => "Dofus client not found",
                Language.Fr => "Client Dofus introuvable",
                _ => "No se encuentra el cliente de Dofus",
            },
            Packs.PackState.NoVersion => i switch
            {
                Language.En => "The client's version file cannot be read",
                Language.Fr => "Le fichier de version du client est illisible",
                _ => "No se puede leer el fichero de versión del cliente",
            },
            Packs.PackState.Missing => i switch
            {
                Language.En => "Not installed",
                Language.Fr => "Non installé",
                _ => "Sin instalar",
            },
            Packs.PackState.Partial => i switch
            {
                Language.En => "Incomplete: resume to finish it",
                Language.Fr => "Incomplet : reprends pour le terminer",
                _ => "A medias: reanuda para terminarlo",
            },
            Packs.PackState.Unverified when s.VerifiedVersion.Length > 0 => i switch
            {
                Language.En => $"Verified for {s.VerifiedVersion}; the client is now {s.ClientVersion}. Verify it again",
                Language.Fr => $"Vérifié pour {s.VerifiedVersion} ; le client est maintenant en {s.ClientVersion}. Vérifie-le à nouveau",
                _ => $"Verificado para la {s.VerifiedVersion}; el cliente ya es la {s.ClientVersion}. Vuelve a verificarlo",
            },
            Packs.PackState.Unverified => i switch
            {
                Language.En => $"On disk, not verified for {s.ClientVersion}",
                Language.Fr => $"Sur le disque, non vérifié pour {s.ClientVersion}",
                _ => $"En el disco, sin verificar para la {s.ClientVersion}",
            },
            _ => i switch
            {
                Language.En => $"Installed and verified for {s.ClientVersion}",
                Language.Fr => $"Installé et vérifié pour {s.ClientVersion}",
                _ => $"Instalado y verificado para la {s.ClientVersion}",
            },
        };

        public static string ProgresoDePack(Language i, Packs.PackProgress p)
        {
            string percent = (p.Fraction * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %";
            return p.Phase switch
            {
                Packs.PackPhase.Manifest => i switch
                {
                    Language.En => "Reading Ankama's index… " + percent,
                    Language.Fr => "Lecture de l'index d'Ankama… " + percent,
                    _ => "Leyendo el índice de Ankama… " + percent,
                },
                Packs.PackPhase.Checking => i switch
                {
                    Language.En => "Checking the files on disk… " + percent,
                    Language.Fr => "Vérification des fichiers présents… " + percent,
                    _ => "Comprobando los ficheros del disco… " + percent,
                },
                Packs.PackPhase.Downloading => i switch
                {
                    Language.En => $"Downloading… {percent} · {Tamano(p.Done)} of {Tamano(p.Total)} · {Tamano((long)p.BytesPerSecond)}/s",
                    Language.Fr => $"Téléchargement… {percent} · {Tamano(p.Done)} sur {Tamano(p.Total)} · {Tamano((long)p.BytesPerSecond)}/s",
                    _ => $"Descargando… {percent} · {Tamano(p.Done)} de {Tamano(p.Total)} · {Tamano((long)p.BytesPerSecond)}/s",
                },
                _ => i switch
                {
                    Language.En => "Verifying… " + percent,
                    Language.Fr => "Vérification… " + percent,
                    _ => "Verificando… " + percent,
                },
            };
        }

        /// <summary>The main button of a pack says what it will do in the state it is in.</summary>
        public static string AccionDePack(Language i, Packs.PackState state) => state switch
        {
            Packs.PackState.Partial => i switch
            {
                Language.En => "Resume",
                Language.Fr => "Reprendre",
                _ => "Reanudar",
            },
            Packs.PackState.Unverified or Packs.PackState.Installed => i switch
            {
                Language.En => "Verify",
                Language.Fr => "Vérifier",
                _ => "Verificar",
            },
            _ => i switch
            {
                Language.En => "Download",
                Language.Fr => "Télécharger",
                _ => "Descargar",
            },
        };

        public static string Cancelar(Language i) => i switch
        {
            Language.En => "Cancel",
            Language.Fr => "Annuler",
            _ => "Cancelar",
        };

        public static string QuitarPack(Language i) => i switch
        {
            Language.En => "Remove",
            Language.Fr => "Supprimer",
            _ => "Quitar",
        };

        public static string UsarPack(Language i) => i switch
        {
            Language.En => "Offer it to the game",
            Language.Fr => "Le proposer au jeu",
            _ => "Ofrecérselo al juego",
        };

        public static string PackEnPausa(Language i) => i switch
        {
            Language.En => "Paused. Resume continues where it stopped.",
            Language.Fr => "En pause. Reprendre continue là où il s'est arrêté.",
            _ => "En pausa. Reanudar sigue donde se quedó.",
        };

        public static string ConfirmarQuitarPack(Language i, Packs.TexturePack pack, long? size)
        {
            string name = NombreDePack(i, pack);
            string frees = size is long bytes ? " (" + Tamano(bytes) + ")" : "";
            return i switch
            {
                Language.En => $"Remove the {name}{frees}? You can download it again later.",
                Language.Fr => $"Supprimer le {name}{frees} ? Tu pourras le télécharger à nouveau plus tard.",
                _ => $"¿Quitar el {name}{frees}? Se puede volver a descargar más adelante.",
            };
        }

        /// <summary>Why a pack operation stopped, in words the player can act on.</summary>
        public static string ErrorDePack(Language i, Packs.PackException e) => e.Error switch
        {
            Packs.PackError.NoClient => i switch
            {
                Language.En => "Dofus.exe was not found. Choose where the client is first.",
                Language.Fr => "Dofus.exe est introuvable. Indique d'abord où est le client.",
                _ => "No se encuentra Dofus.exe. Elige primero dónde está el cliente.",
            },
            Packs.PackError.NoVersion => i switch
            {
                Language.En => $"The client's version cannot be read ({e.Detail}), so the pack cannot be matched to it. Nothing was downloaded.",
                Language.Fr => $"Impossible de lire la version du client ({e.Detail}) : le pack ne peut pas lui être associé. Rien n'a été téléchargé.",
                _ => $"No se puede leer la versión del cliente ({e.Detail}), así que no hay con qué casar el pack. No se ha descargado nada.",
            },
            Packs.PackError.ManifestUnavailable => i switch
            {
                Language.En => $"Ankama's servers did not give the index for this client version ({e.Detail}). Nothing was downloaded.",
                Language.Fr => $"Les serveurs d'Ankama n'ont pas fourni l'index de cette version du client ({e.Detail}). Rien n'a été téléchargé.",
                _ => $"Los servidores de Ankama no han dado el índice de esta versión del cliente ({e.Detail}). No se ha descargado nada.",
            },
            Packs.PackError.ManifestCorrupt or Packs.PackError.FragmentMissing or Packs.PackError.BadManifest => i switch
            {
                Language.En => $"Ankama's index for this client version cannot be used ({e.Detail}).",
                Language.Fr => $"L'index d'Ankama pour cette version du client est inutilisable ({e.Detail}).",
                _ => $"El índice de Ankama para esta versión del cliente no sirve ({e.Detail}).",
            },
            Packs.PackError.ClientRunning => i switch
            {
                Language.En => "Close Dofus first: the game keeps the texture files open.",
                Language.Fr => "Ferme d'abord Dofus : le jeu garde les fichiers de textures ouverts.",
                _ => "Cierra antes Dofus: el juego tiene abiertos los ficheros de texturas.",
            },
            Packs.PackError.NotEnoughSpace => i switch
            {
                Language.En => $"Not enough disk space: {Tamano(e.Needed)} needed, {Tamano(e.Free)} free.",
                Language.Fr => $"Pas assez d'espace disque : {Tamano(e.Needed)} nécessaires, {Tamano(e.Free)} libres.",
                _ => $"No hay sitio en el disco: hacen falta {Tamano(e.Needed)} y hay {Tamano(e.Free)} libres.",
            },
            Packs.PackError.VerifyFailed => i switch
            {
                Language.En => $"{e.Detail} did not match Ankama's hash and will be downloaded again. Press Resume.",
                Language.Fr => $"{e.Detail} ne correspond pas à l'empreinte d'Ankama et sera retéléchargé. Appuie sur Reprendre.",
                _ => $"{e.Detail} no cuadra con la huella de Ankama y se volverá a descargar. Pulsa Reanudar.",
            },
            Packs.PackError.DiskError => i switch
            {
                Language.En => $"Could not write to the disk: {e.Detail}",
                Language.Fr => $"Impossible d'écrire sur le disque : {e.Detail}",
                _ => $"No se ha podido escribir en el disco: {e.Detail}",
            },
            _ => i switch
            {
                Language.En => $"The download stopped after several attempts ({e.Detail}). Resume continues where it stopped.",
                Language.Fr => $"Le téléchargement s'est arrêté après plusieurs essais ({e.Detail}). Reprendre continue là où il s'est arrêté.",
                _ => $"La descarga se ha parado tras varios intentos ({e.Detail}). Reanudar sigue donde se quedó.",
            },
        };

        /// <summary>A size the way a player reads it.</summary>
        public static string Tamano(long bytes)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return bytes switch
            {
                >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", c) + " GB",
                >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", c) + " MB",
                >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", c) + " KB",
                _ => bytes + " B",
            };
        }
    }
}
