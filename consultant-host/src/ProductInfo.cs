namespace OdooConsultor
{
    /// <summary>
    /// Metadata y constantes centralizadas de identidad y versión del producto.
    /// </summary>
    public static class ProductInfo
    {
        public const string Name = "OdooConsultor";
        public const string McpServerName = "odoo-consultor";
        public const string Version = "1.0.0";
        public const string FullVersion = "1.0.0.0";
        public const string UserAgent = "Odoo-Consultor-Copilot/" + Version;
        public const string Banner = Name + " " + Version;
        public const string SupportedOdooSeries = "14.0 a 19.0";
        public const string McpInstructions = "Copiloto funcional controlado para Odoo 14 a 19 (Enterprise y Community). Investiga con fuentes versionadas, diagnostica brechas, prepara demostraciones y puede instalar módulos oficiales únicamente en staging mediante planes confirmados. Nunca elige métodos de Odoo. Producción es solo lectura; toda escritura exige paquete certificado y confirmación exacta. No solicites ni expongas API keys.";
    }
}
