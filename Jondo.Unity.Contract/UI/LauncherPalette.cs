using System;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// La paleta del lanzador, en numeros y sin toolkit.
    /// </summary>
    /// <remarks>
    /// Todos los valores salen de la hoja de estilos de la interfaz web original
    /// (launcher_assets/index.html) y estaban escritos como <c>Color.FromArgb</c> dentro de
    /// LauncherTheme, que es de Windows Forms. Al migrar el lanzador a Avalonia habria hecho falta
    /// copiarlos, y una copia se separa del original en cuanto alguien toca uno de los dos.
    ///
    /// Aqui son <c>uint</c> en formato 0xAARRGGBB, que es lo que entienden los dos: Windows Forms
    /// hace <c>Color.FromArgb((int)valor)</c> y Avalonia <c>Color.FromUInt32(valor)</c>. El
    /// servidor y el lanzador siguen pintando igual porque pintan con lo mismo, que era justo la
    /// idea de tenerlo compartido.
    /// </remarks>
    public static class LauncherPalette
    {
        public const uint Background = 0xFF0D0603;

        public const uint CardFill = 0x85100905;

        public const uint ConsoleFill = 0x8C0C0704;

        public const uint BarFill = 0xBF1A0F08;

        public const uint ConsoleBackground = 0xFF18100A;

        /// <summary>#e6b800</summary>
        public const uint GoldBorder = 0xFFE6B800;

        /// <summary>#ffcc00</summary>
        public const uint LightGold = 0xFFFFCC00;

        /// <summary>#d4af37</summary>
        public const uint Gold = 0xFFD4AF37;

        /// <summary>#e6c280</summary>
        public const uint SoftGold = 0xFFE6C280;

        /// <summary>#b89865</summary>
        public const uint MutedGold = 0xFFB89865;

        /// <summary>#593c1d</summary>
        public const uint LightBrown = 0xFF593C1D;

        public const uint LightBrownText = 0xFF967E5C;

        /// <summary>#7a5328</summary>
        public const uint BorderBrown = 0xFF7A5328;

        /// <summary>#fff3d6</summary>
        public const uint BaseText = 0xFFFFF3D6;

        /// <summary>#fff3cc</summary>
        public const uint CardText = 0xFFFFF3CC;

        /// <summary>#ffe680</summary>
        public const uint HighlightText = 0xFFFFE680;

        /// <summary>#fff8e7</summary>
        public const uint FieldText = 0xFFFFF8E7;

        /// <summary>rgba(12,6,3,0.85)</summary>
        public const uint FieldBackground = 0xD90C0603;

        /// <summary>rgba(20,10,5,0.5)</summary>
        public const uint DisabledFieldBackground = 0x80140A05;

        /// <summary>#44301a</summary>
        public const uint DisabledFieldBorder = 0xFF44301A;

        /// <summary>#776655</summary>
        public const uint DisabledFieldText = 0xFF776655;

        /// <summary>#7db326</summary>
        public const uint GreenTop = 0xFF7DB326;

        /// <summary>#466c14</summary>
        public const uint GreenBottom = 0xFF466C14;

        /// <summary>#a3e03b</summary>
        public const uint GreenBorder = 0xFFA3E03B;

        public const uint GreenTopHover = 0xFF91CE2C;

        public const uint GreenBottomHover = 0xFF558319;

        /// <summary>#a040a0</summary>
        public const uint PurpleTop = 0xFFA040A0;

        /// <summary>#602060</summary>
        public const uint PurpleBottom = 0xFF602060;

        /// <summary>#d070d0</summary>
        public const uint PurpleBorder = 0xFFD070D0;

        public const uint GrayTop = 0xFF444444;

        public const uint GrayBottom = 0xFF222222;

        public const uint GrayBorder = 0xFF555555;

        public const uint GrayText = 0xFF888888;

        /// <summary>#ff4d4d</summary>
        public const uint Red = 0xFFFF4D4D;

        /// <summary>#92d050</summary>
        public const uint OnlineGreen = 0xFF92D050;

        /// <summary>#50ff50</summary>
        public const uint DotGreen = 0xFF50FF50;

        /// <summary>rgba(140,20,20,0.9)</summary>
        public const uint AlertBackground = 0xE68C1414;

        /// <summary>#ffe6e6</summary>
        public const uint AlertText = 0xFFFFE6E6;

        public const uint LogHaapi = 0xFF00F0FF;

        public const uint LogZaap = 0xFF33CCFF;

        public const uint LogServer = 0xFFFFCC00;

        public const uint LogSuccess = 0xFF50FF50;

        public const uint LogError = 0xFFFF4D4D;

        public const uint LogNormal = 0xFFD1BA86;

        public const uint LogTime = 0xFF7A6548;
    }
}
