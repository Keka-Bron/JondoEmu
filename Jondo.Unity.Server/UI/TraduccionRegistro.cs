using System;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Traduce al INGLÉS o al FRANCÉS las líneas del registro que la ventana del servidor enseña,
    /// cuando el idioma elegido no es el español en el que el servidor las escribe.
    ///
    /// CÓMO: por fragmentos. Cada par lleva un trozo de frase español y su traducción, y la línea
    /// se va reescribiendo con todos los que aparezcan; los números y los identificadores que van
    /// alrededor quedan donde estaban. Es lo que permite traducir líneas que el servidor compone
    /// con datos dentro («Turno de 13825560 (jugador), 400 décimas»), que un diccionario de líneas
    /// enteras no puede tocar.
    ///
    /// LÍMITES, dichos en claro: traducir TODO el registro sería traducir el emulador entero, y no
    /// es lo que esto pretende. Lo que cubre la tabla es lo que se ve de verdad en una sesión
    /// normal —el arranque, el combate, el movimiento, el canal de mando—; lo que no está en la
    /// tabla sale tal cual, en español, que sigue siendo legible para quien lleva el servidor.
    ///
    /// El orden importa: los fragmentos largos se aplican antes que los cortos, para que
    /// «El jugador se declara listo» no se coma antes «El jugador» a secas.
    /// </summary>
    internal static class TraduccionRegistro
    {
        private sealed class Par
        {
            public string Es = "", En = "", Fr = "";
        }

        private static readonly Par[] Pares =
        {
            // ─── El arranque ──────────────────────────────────────────────────────────────────
            new() { Es = "Inicializando la base de datos", En = "Initializing the database", Fr = "Initialisation de la base de données" },
            new() { Es = "Bases de datos inicializadas correctamente", En = "Databases initialized successfully", Fr = "Bases de données initialisées avec succès" },
            new() { Es = "Extrayendo world.db completo de world.zip", En = "Extracting full world.db from world.zip", Fr = "Extraction du world.db complet depuis world.zip" },
            new() { Es = "extraído de world.zip", En = "extracted from world.zip", Fr = "extrait du world.zip" },
            new() { Es = "Cargando datos de SQLite", En = "Loading data from SQLite", Fr = "Chargement des données depuis SQLite" },
            new() { Es = "cargado(s) desde la base de datos", En = "loaded from the database", Fr = "chargés depuis la base de données" },
            new() { Es = "de la base de datos", En = "from the database", Fr = "de la base de données" },
            new() { Es = "Ids de grupo repartidos hasta el", En = "Group ids handed out up to", Fr = "Ids de groupe attribués jusqu'à" },
            new() { Es = "los que se generen al vuelo siguen por debajo", En = "the ones generated on the fly continue below", Fr = "ceux générés à la volée continuent en dessous" },
            new() { Es = "datos de celdas caminables", En = "walkable cell data", Fr = "données de cellules praticables" },
            new() { Es = "datos de combate", En = "fight data", Fr = "données de combat" },
            new() { Es = "registros de información de mapa", En = "map info records", Fr = "enregistrements d'information de carte" },
            new() { Es = "registros de acciones de desplazamiento", En = "map scroll action records", Fr = "enregistrements d'actions de défilement" },
            new() { Es = "tabla de experiencia cargada", En = "experience table loaded", Fr = "table d'expérience chargée" },
            new() { Es = "objetos de montura con su aspecto", En = "mount items with their look", Fr = "objets de monture avec leur aspect" },
            new() { Es = "Cargando los bloques de entrada al mundo", En = "Loading the world entry blocks", Fr = "Chargement des blocs d'entrée au monde" },
            new() { Es = "Todos los servicios de emulación EN MARCHA", En = "ALL EMULATION SERVICES ONLINE AND READY", Fr = "TOUS LES SERVICES D'ÉMULATION SONT EN LIGNE" },

            // ─── El combate ───────────────────────────────────────────────────────────────────
            new() { Es = "Esperando a que el cliente pida los actores", En = "Waiting for the client to ask for the actors", Fr = "En attente que le client demande les acteurs" },
            new() { Es = "Preparación del combate", En = "Fight preparation", Fr = "Préparation du combat" },
            new() { Es = "casillas azules y", En = "blue cells and", Fr = "cases bleues et" },
            new() { Es = "rojas", En = "red", Fr = "rouges" },
            new() { Es = "El jugador se coloca en la casilla", En = "The player places on cell", Fr = "Le joueur se place sur la case" },
            new() { Es = "(venía de la", En = "(was on the", Fr = "(venait de la" },
            new() { Es = "El jugador se declara listo", En = "The player declares ready", Fr = "Le joueur se déclare prêt" },
            new() { Es = "Empieza el combate", En = "The fight starts", Fr = "Le combat commence" },
            new() { Es = "combatientes, primero", En = "fighters, first", Fr = "combattants, premier" },
            new() { Es = "Turno de", En = "Turn of", Fr = "Tour de" },
            new() { Es = "(jugador)", En = "(player)", Fr = "(joueur)" },
            new() { Es = "décimas", En = "tenths", Fr = "dixièmes" },
            new() { Es = "puesto", En = "slot", Fr = "position" },
            new() { Es = "Anda hasta la casilla", En = "Walks to cell", Fr = "Marche jusqu'à la case" },
            new() { Es = "pasos", En = "steps", Fr = "pas" },
            new() { Es = "le quedan", En = "remaining", Fr = "il lui reste" },
            new() { Es = "Lanza el hechizo", En = "Casts spell", Fr = "Lance le sort" },
            new() { Es = "grado", En = "grade", Fr = "rang" },
            new() { Es = "a la casilla", En = "on cell", Fr = "sur la case" },
            new() { Es = "todavía no se sabe aplicar; se manda al panel tal cual", En = "still not known how to apply; sent to the panel as-is", Fr = "on ne sait pas encore l'appliquer ; envoyé au panneau tel quel" },
            new() { Es = "Buff", En = "Buff", Fr = "Bonus" },

            // ─── El mundo y el movimiento ────────────────────────────────────────────────────
            new() { Es = "[Personajes]", En = "[Characters]", Fr = "[Personnages]" },
            new() { Es = "Hueco adicional confirmado para la cuenta", En = "Additional slot confirmed for account", Fr = "Emplacement supplémentaire confirmé pour le compte" },
            new() { Es = "en el servidor", En = "on server", Fr = "sur le serveur" },
            new() { Es = "El máximo de personajes", En = "The character limit", Fr = "La limite de personnages" },
            new() { Es = "personajes en esta cuenta", En = "characters on this account", Fr = "personnages sur ce compte" },
            new() { Es = "El nombre ya está cogido", En = "The name is already taken", Fr = "Le nom est déjà utilisé" },
            new() { Es = "Nombre sugerido", En = "Suggested name", Fr = "Nom suggéré" },
            new() { Es = "Sin kamas, equipo ni progreso de cuenta", En = "With no kamas, equipment, or account progress", Fr = "Sans kamas, équipement ni progression de compte" },
            new() { Es = "Cambio de mapa", En = "Map change", Fr = "Changement de carte" },
            new() { Es = "llegando a la casilla", En = "arriving on cell", Fr = "arrivée sur la case" },
            new() { Es = "Esperando al jrh", En = "Waiting for jrh", Fr = "En attente du jrh" },
            new() { Es = "Actores del mapa enviados", En = "Actors of map sent", Fr = "Acteurs de la carte envoyés" },
            new() { Es = "en la casilla", En = "on cell", Fr = "sur la case" },
            new() { Es = "Al mapa", En = "To map", Fr = "Vers la carte" },
            new() { Es = "Se ha creado", En = "Created", Fr = "Créé" },
            new() { Es = "raza", En = "breed", Fr = "race" },
            new() { Es = "en el zaap de Astrub, con el conjunto del aventurero", En = "at the Astrub zaap, with the adventurer set", Fr = "au zaap d'Astrub, avec la panoplie de l'aventurier" },
            new() { Es = "El cliente ha cerrado la conexión Thrift", En = "The client closed the Thrift connection", Fr = "Le client a fermé la connexion Thrift" },

            // ─── El canal de mando y los permisos ────────────────────────────────────────────
            new() { Es = "ha intentado", En = "tried to use", Fr = "a essayé d'utiliser" },
            new() { Es = "Rechazado", En = "Rejected", Fr = "Rejeté" },
            new() { Es = "ha reventado", En = "crashed", Fr = "a planté" },
            new() { Es = "El lanzador pide apagar el servidor", En = "The launcher asks to shut the server down", Fr = "Le lanceur demande l'arrêt du serveur" },
            new() { Es = "pasa a", En = "becomes", Fr = "devient" },
            new() { Es = "ha sido expulsada desde el panel", En = "has been kicked from the panel", Fr = "a été expulsée depuis le panneau" },
            new() { Es = "ha fallado", En = "has failed", Fr = "a échoué" },
        };

        /// <summary>
        /// La línea traducida al idioma pedido. Con español se devuelve tal cual, que es como se
        /// escribió; con inglés o francés se reescribe con los fragmentos que se conozcan y el
        /// resto queda en español.
        /// </summary>
        public static string Traducir(string linea, Language idioma)
        {
            if (idioma == Language.Es || linea.Length == 0) return linea;

            foreach (var par in Pares)
            {
                if (par.Es.Length == 0 || !linea.Contains(par.Es, StringComparison.Ordinal)) continue;
                linea = linea.Replace(par.Es, idioma == Language.En ? par.En : par.Fr,
                                      StringComparison.Ordinal);
            }
            return linea;
        }
    }
}
