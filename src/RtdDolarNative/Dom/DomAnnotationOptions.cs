namespace RtdDolarNative.Dom
{
    public sealed class DomAnnotationOptions
    {
        public DomAnnotationOptions()
        {
            ShowBase = true;
            ShowGarman = true;
            ShowGauss = true;
            ShowStdDev = true;
            ShowGarch = true;
            ShowPercent = true;
            ShowMaxMin7 = true;
            ShowProfile = true;
            ShowTechnical = true;
            ShowFlow = true;
        }

        public bool ShowBase { get; set; }
        public bool ShowGarman { get; set; }
        public bool ShowGauss { get; set; }
        public bool ShowStdDev { get; set; }
        public bool ShowGarch { get; set; }
        public bool ShowPercent { get; set; }
        public bool ShowMaxMin7 { get; set; }
        public bool ShowProfile { get; set; }
        public bool ShowTechnical { get; set; }
        public bool ShowFlow { get; set; }
    }
}
