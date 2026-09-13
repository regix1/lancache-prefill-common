namespace LancachePrefill.Common
{
    public sealed class PrefillSummaryResult
    {
        public int AlreadyUpToDate { get; set; }
        public int FailedApps { get; set; }
        public int Updated { get; set; }
        public int UnownedAppsSkipped { get; set; }

        private int _totalGamesPrefilled => AlreadyUpToDate + FailedApps + Updated;

        public ByteSize TotalBytesTransferred { get; set; }
        public Stopwatch PrefillElapsedTime { get; } = Stopwatch.StartNew();

        public void RenderSummaryTable(IAnsiConsole ansiConsole)
        {
            ArgumentNullException.ThrowIfNull(ansiConsole);

            var table = new Table
            {
                Border = TableBorder.MinimalHeavyHead
            };

            var rowFields = new List<string>();

            // Number of updated apps
            table.AddColumn(new TableColumn(Cyan("Updated")).Centered());
            rowFields.Add(Updated.ToString(CultureInfo.CurrentCulture));

            // Apps already up to date
            table.AddColumn(new TableColumn(Green("Up To Date")).Centered());
            rowFields.Add(AlreadyUpToDate.ToString(CultureInfo.CurrentCulture));

            // Failed
            if (FailedApps > 0)
            {
                table.AddColumn(new TableColumn(Red("Failed")).Centered());
                rowFields.Add(FailedApps.ToString(CultureInfo.CurrentCulture));
            }

            // Unowned
            if (UnownedAppsSkipped > 0)
            {
                table.AddColumn(new TableColumn(LightYellow("Unowned")).Centered());
                rowFields.Add(UnownedAppsSkipped.ToString(CultureInfo.CurrentCulture));
            }
            table.AddRow(rowFields.ToArray());

            var totalBytesTransferred = TotalBytesTransferred;
            var grid = new Grid()
                       .AddColumn(new GridColumn())
                       .AddRow($" Prefilled {Magenta(_totalGamesPrefilled)} apps totaling {Magenta(totalBytesTransferred.ToDecimalString())} in {LightYellow(PrefillElapsedTime.FormatElapsedString())}")
                       .AddRow(table);

            ansiConsole.Write(new Rule());
            ansiConsole.Write(new Padder(grid, new Padding(1, 0)));
        }
    }
}
