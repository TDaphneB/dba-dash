﻿using DBADashGUI.Performance;
using DBADashGUI.Theme;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Navigation;
using DBADash;
using DocumentFormat.OpenXml.Vml.Office;
using Microsoft.SqlServer.Management.SqlScriptPublish;

namespace DBADashGUI.CustomReports
{
    public partial class CustomReportView : UserControl, ISetContext, IRefreshData
    {
        public event EventHandler ReportNameChanged;

        private ContextMenuStrip columnContextMenu;
        private ContextMenuStrip cellContextMenu;
        private readonly ToolStripMenuItem convertLocalMenuItem = new("Convert to local timezone") { Checked = true, CheckOnClick = true };
        private readonly ToolStripMenuItem filterLike = new("LIKE", Properties.Resources.FilteredTextBox_16x);
        private int clickedColumnIndex = -1;
        private int clickedRowIndex = -1;
        private DataSet reportDS;
        private int selectedTableIndex = 0;
        private bool doAutoSize = true;
        private bool suppressCboResultsIndexChanged;

        public CustomReportView()
        {
            InitializeComponent();
            ShowParamPrompt(false);
            dgv.AutoGenerateColumns = false;
            this.ApplyTheme();
        }

        private void InitializeContextMenu()
        {
            columnContextMenu = new ContextMenuStrip();
            if (report.CanEditReport)
            {
                var renameColumnMenuItem = new ToolStripMenuItem("Rename Column", Properties.Resources.Rename_16x);
                var setFormatStringMenuItem =
                    new ToolStripMenuItem("Set Format String", Properties.Resources.Percentage_16x);
                var addLink = new ToolStripMenuItem("Add Link", Properties.Resources.WebURL_16x);
                var rules = new ToolStripMenuItem("Highlighting Rules", Properties.Resources.HighlightHS);
                renameColumnMenuItem.Click += RenameColumnMenuItem_Click;
                convertLocalMenuItem.Click += ConvertLocalMenuItem_Click;
                setFormatStringMenuItem.Click += SetFormatStringMenuItem_Click;
                addLink.Click += AddLink_Click;
                rules.Click += SetCellHighlightingRules;
                columnContextMenu.Items.Add(renameColumnMenuItem);
                columnContextMenu.Items.Add(convertLocalMenuItem);
                columnContextMenu.Items.Add(setFormatStringMenuItem);
                columnContextMenu.Items.Add(addLink);
                columnContextMenu.Items.Add(rules);
                dgv.MouseUp += Dgv_MouseUp;
            }

            var highlight = new ToolStripMenuItem("Highlight", Properties.Resources.HighlightHS);
            var filterByValue = new ToolStripMenuItem("Filter By Value", Properties.Resources.Filter_16x) { Tag = false };
            var excludeValue = new ToolStripMenuItem("Exclude Value", Properties.Resources.StopFilter_16x) { Tag = true };
            cellContextMenu = new ContextMenuStrip();
            cellContextMenu.Items.Add(filterByValue);
            cellContextMenu.Items.Add(excludeValue);
            cellContextMenu.Items.Add(filterLike);
            if (report.CanEditReport)
            {
                cellContextMenu.Items.Add(highlight);
            }
            highlight.Click += SetCellHighlightingRules;
            excludeValue.Click += FilterByValue_Click;
            filterByValue.Click += FilterByValue_Click;
            filterLike.Click += FilterLike_Click;
            dgv.MouseUp += Dgv_MouseUp;
        }

        private void FilterLike_Click(object sender, EventArgs e)
        {
            var value = dgv.Rows[clickedRowIndex].Cells[clickedColumnIndex].Value.DBNullToNull()?.ToString();
            var colName = dgv.Columns[clickedColumnIndex].DataPropertyName;

            if (CommonShared.ShowInputDialog(ref value, "Enter value to filter by:", default, "Use % or * as wildcards") == DialogResult.Cancel) return;
            if (string.IsNullOrEmpty(value)) return;
            if (dgv.DataSource is not DataView dv) return;
            var filter = dv.RowFilter;
            if (!string.IsNullOrEmpty(filter))
            {
                filter += Environment.NewLine + " AND ";
            }
            value = EscapeValue(value);
            filter += $"{colName} LIKE {value}";
            SetFilter(filter);
        }

        private void SetFilter(string filter)
        {
            if (dgv.DataSource is not DataView dv) return;
            var previousFilter = dv.RowFilter;
            try
            {
                dv.RowFilter = filter;
                tsClearFilter.Enabled = true;
                tsClearFilter.Font = new Font(tsClearFilter.Font, FontStyle.Bold);
                tsClearFilter.ToolTipText = dv.RowFilter;
            }
            catch (Exception ex)
            {
                dv.RowFilter = previousFilter;
                MessageBox.Show("Error setting row filter: " + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void FilterByValue_Click(object sender, EventArgs e)
        {
            var exclude = (bool)((ToolStripMenuItem)sender).Tag;
            var value = dgv.Rows[clickedRowIndex].Cells[clickedColumnIndex].Value;
            var colName = dgv.Columns[clickedColumnIndex].DataPropertyName;
            colName = EscapeColumnName(colName);
            var filterValue = FormatFilterValue(value, exclude);

            if (dgv.DataSource is not DataView dv) return;
            var filter = dv.RowFilter;
            if (!string.IsNullOrEmpty(filter))
            {
                filter += Environment.NewLine + " AND ";
            }

            filter += $"{colName} {filterValue}";
            SetFilter(filter);
        }

        private static string EscapeColumnName(string columnName)
        {
            return "[" + columnName.Replace("]", "]]") + "]";
        }

        private static string EscapeValue(string value) => "'" + value.ToString()?.Replace("'", "''") + "'";

        private static string FormatFilterValue(object value, bool exclude)
        {
            var compare = (exclude ? "<>" : "=");
            if (value.DBNullToNull() is null)
            {
                return exclude ? "IS NOT NULL" : "IS NULL";
            }
            else if (value.GetType().IsNumericType())
            {
                return compare + value.ToString();
            }
            else
            {
                return $"{compare} " + EscapeValue(value.ToString());
            }
        }

        private void SetCellHighlightingRules(object sender, EventArgs e)
        {
            try
            {
                var customReportResult = report.CustomReportResults[selectedTableIndex];
                var columnName = dgv.Columns[clickedColumnIndex].DataPropertyName;
                customReportResult.CellHighlightingRules.TryGetValue(dgv.Columns[clickedColumnIndex].DataPropertyName,
                    out var ruleSet);

                var frm = new CellHighlightingRulesConfig()
                {
                    ColumnList = dgv.Columns,
                    CellHighlightingRules = new KeyValuePair<string, CellHighlightingRuleSet>(columnName,
                        ruleSet.DeepCopy() ?? new CellHighlightingRuleSet() { TargetColumn = columnName }),
                    CellValue = clickedRowIndex >= 0 ? dgv.Rows[clickedRowIndex].Cells[clickedColumnIndex].Value : null,
                    CellValueIsNull = clickedRowIndex >= 0 &&
                                      dgv.Rows[clickedRowIndex].Cells[clickedColumnIndex].Value.DBNullToNull() == null
                };

                frm.ShowDialog();
                if (frm.DialogResult != DialogResult.OK) return;
                report.CustomReportResults[selectedTableIndex].CellHighlightingRules.Remove(columnName);
                if (frm.CellHighlightingRules.Value is { HasRules: true })
                {
                    report.CustomReportResults[selectedTableIndex].CellHighlightingRules
                        .Add(columnName, frm.CellHighlightingRules.Value);
                }

                report.Update();
                ShowTable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error setting highlighting rules: " + ex.Message, "Error", MessageBoxButtons.OK,
                                       MessageBoxIcon.Error);
            }
        }

        private void AddLink_Click(object sender, EventArgs e)
        {
            try
            {
                var customReportResult = report.CustomReportResults[selectedTableIndex];
                var col = dgv.Columns[clickedColumnIndex].DataPropertyName;
                var linkColumnInfo = customReportResult.LinkColumns?.ContainsKey(col) == true
                    ? customReportResult.LinkColumns[col]
                    : null;
                var colList = dgv.Columns.Cast<DataGridViewColumn>().Select(c => c.DataPropertyName).ToList();
                var frm = new LinkColumnTypeSelector()
                { LinkColumnInfo = linkColumnInfo, ColumnList = colList, Context = context, LinkColumn = col };
                frm.ShowDialog();
                if (frm.DialogResult != DialogResult.OK) return;
                customReportResult.LinkColumns ??= new();
                if (frm.LinkColumnInfo == null)
                {
                    customReportResult.LinkColumns.Remove(col);
                }
                else if (customReportResult.LinkColumns.ContainsKey(col))
                {
                    customReportResult.LinkColumns[col] = frm.LinkColumnInfo;
                }
                else
                {
                    customReportResult.LinkColumns.Add(col, frm.LinkColumnInfo);
                }

                dgv.Columns.Clear();
                doAutoSize = true;
                report.Update();
                ShowTable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error adding link: " + ex.Message, "Error", MessageBoxButtons.OK,
                                       MessageBoxIcon.Error);
            }
        }

        private void SetFormatStringMenuItem_Click(object sender, EventArgs e)
        {
            if (clickedColumnIndex < 0) return;
            var formatString = dgv.Columns[clickedColumnIndex].DefaultCellStyle.Format;
            var key = dgv.Columns[clickedColumnIndex].Name;
            if (CommonShared.ShowInputDialog(ref formatString, "Enter format string (e.g. N1, P1, yyyy-MM-dd)") !=
                DialogResult.OK) return;
            dgv.Columns[clickedColumnIndex].DefaultCellStyle.Format = formatString;
            if (report.CustomReportResults[selectedTableIndex].CellFormatString.ContainsKey(key))
            {
                report.CustomReportResults[selectedTableIndex].CellFormatString.Remove(key);
            }

            if (!string.IsNullOrEmpty(formatString))
            {
                report.CustomReportResults[selectedTableIndex].CellFormatString.Add(key, formatString);
            }

            report.Update();
        }

        private void ConvertLocalMenuItem_Click(object sender, EventArgs e)
        {
            if (clickedColumnIndex < 0) return;
            var name = dgv.Columns[clickedColumnIndex].DataPropertyName;
            switch (convertLocalMenuItem.Checked)
            {
                case true when report.CustomReportResults[selectedTableIndex].DoNotConvertToLocalTimeZone.Contains(name):
                    report.CustomReportResults[selectedTableIndex].DoNotConvertToLocalTimeZone.Remove(name);
                    break;

                case false when !report.CustomReportResults[selectedTableIndex].DoNotConvertToLocalTimeZone.Contains(name):
                    report.CustomReportResults[selectedTableIndex].DoNotConvertToLocalTimeZone.Add(name);
                    break;
            }

            try
            {
                report.Update();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving time zone preference: " + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            RefreshData();
        }

        /// <summary>
        /// Used to display context menu when user right-clicks column headers.  Selected column index is stored in clickedColumnIndex
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Dgv_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            // Perform a hit test to determine where the click occurred
            var hitTestInfo = dgv.HitTest(e.X, e.Y);
            clickedColumnIndex = hitTestInfo.ColumnIndex;
            clickedRowIndex = hitTestInfo.RowIndex;
            switch (hitTestInfo.Type)
            {
                case DataGridViewHitTestType.Cell:
                    filterLike.Visible = dgv.Columns[clickedColumnIndex].ValueType == typeof(string);
                    cellContextMenu.Show(dgv, e.Location);
                    return;

                case DataGridViewHitTestType.ColumnHeader:
                    {
                        if (dgv.Columns[clickedColumnIndex].ValueType ==
                            typeof(DateTime)) // Show option for timezone conversion if column is DateTime
                        {
                            convertLocalMenuItem.Checked =
                                !report.CustomReportResults[selectedTableIndex].DoNotConvertToLocalTimeZone
                                    .Contains(dgv.Columns[clickedColumnIndex].DataPropertyName);
                            convertLocalMenuItem.Visible = true;
                        }
                        else
                        {
                            convertLocalMenuItem.Visible = false;
                        }

                        // Show the context menu at the mouse position
                        columnContextMenu.Show(dgv, e.Location);
                        break;
                    }
            }
        }

        private void RenameColumnMenuItem_Click(object sender, EventArgs e)
        {
            if (clickedColumnIndex < 0) return;
            // Show a dialog for renaming
            var newName = dgv.Columns[clickedColumnIndex].HeaderText;
            if (CommonShared.ShowInputDialog(ref newName, "Enter new column name:") != DialogResult.OK) return;
            if (string.IsNullOrEmpty(newName))
            {
                newName = dgv.Columns[clickedColumnIndex].DataPropertyName;
            }
            dgv.Columns[clickedColumnIndex].HeaderText = newName;
            try
            {
                report.CustomReportResults[selectedTableIndex].ColumnAlias = GetColumnMapping();
                report.Update();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving alias: " + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private Dictionary<string, string> GetColumnMapping() => dgv.Columns.Cast<DataGridViewColumn>()
                .Where(column => column.DataPropertyName != column.HeaderText)
                .ToDictionary(column => column.DataPropertyName, column => column.HeaderText);

        /// <summary>
        /// Param prompt is shown instead of grid if we have a 201 error indicating that parameter values are required.
        /// </summary>
        /// <param name="show">True = parameter prompt is visible.  False = Grid is visible</param>
        private void ShowParamPrompt(bool show)
        {
            splitContainer1.Panel2Collapsed = !show;
            splitContainer1.Panel1Collapsed = show;
        }

        /// <summary>
        /// Convert DateTime columns to local timezone unless user has specified that conversion should be excluded for the column
        /// </summary>
        /// <param name="dt"></param>
        private void ConvertDateTimeColsToLocalTimeZone(DataTable dt)
        {
            var convertCols = dt.Columns.Cast<DataColumn>()
                .Where(column => column.DataType == typeof(DateTime) && !report.CustomReportResults[selectedTableIndex].DoNotConvertToLocalTimeZone.Contains(column.ColumnName))
                .Select(column => column.ColumnName).ToList();
            if (convertCols.Any())
            {
                DateHelper.ConvertUTCToAppTimeZone(ref dt, convertCols);
            }
        }

        private void SetDataSource(DataTable dt)
        {
            if (dgv.Columns.Count == 0)
            {
                AddColumns(dt);
                dgv.ApplyTheme();
            }
            dgv.DataSource = null;
            dgv.DataSource = new DataView(dt);
            tsClearFilter.Enabled = false;
            tsClearFilter.Font = new Font(tsClearFilter.Font, FontStyle.Regular);
            tsClearFilter.ToolTipText = string.Empty;
        }

        /// <summary>
        /// Add columns to data grid view based on the columns in the data table and user preferences for column alias, cell format string and link columns
        /// </summary>
        /// <param name="dt"></param>
        private void AddColumns(DataTable dt)
        {
            var customReportResults = report.CustomReportResults[selectedTableIndex];
            foreach (DataColumn dataColumn in dt.Columns)
            {
                DataGridViewColumn column;

                if (customReportResults.LinkColumns.ContainsKey(dataColumn.ColumnName))
                {
                    column = new DataGridViewLinkColumn();
                }
                else if (dataColumn.DataType == typeof(bool))
                {
                    column = new DataGridViewCheckBoxColumn();
                }
                else
                {
                    column = new DataGridViewTextBoxColumn();
                }

                column.SortMode = DataGridViewColumnSortMode.Automatic;
                column.DefaultCellStyle.Format =
                    customReportResults.CellFormatString
                        .ContainsKey(dataColumn.ColumnName)
                        ? customReportResults.CellFormatString[dataColumn.ColumnName]
                        : "";

                column.DataPropertyName = dataColumn.ColumnName;
                column.Name = dataColumn.ColumnName;
                column.HeaderText =
                    customReportResults.ColumnAlias.ContainsKey(column.DataPropertyName)
                        ? customReportResults.ColumnAlias[column.DataPropertyName]
                        : dataColumn.Caption;
                column.ValueType = dataColumn.DataType;
                dgv.Columns.Add(column);
            }
        }

        /// <summary>
        /// Results combobox allows user to select which result set to display if the report returns multiple result sets
        /// </summary>
        private void LoadResultsCombo()
        {
            suppressCboResultsIndexChanged = true;
            for (var i = 0; i < reportDS.Tables.Count; i++)
            {
                if (report.CustomReportResults.ContainsKey(i))
                {
                    report.CustomReportResults[i].ResultName ??= "Result" + i; // ensure we have a name
                }
                else
                {
                    report.CustomReportResults.Add(i, new CustomReportResult() { ResultName = "Result" + i });
                }
            }

            cboResults.Items.Clear();
            for (var i = 0; i < reportDS.Tables.Count; i++)
            {
                cboResults.Items.Add(report.CustomReportResults[i].ResultName);
            }
            renameResultSetToolStripMenuItem.Visible = cboResults.Items.Count > 1;
            cboResults.Visible = cboResults.Items.Count > 1;
            lblSelectResults.Visible = cboResults.Items.Count > 1;
            cboResults.SelectedIndex = selectedTableIndex < cboResults.Items.Count ? selectedTableIndex : 0;
            suppressCboResultsIndexChanged = false;
        }

        public void RefreshData()
        {
            try
            {
                ShowParamPrompt(false); // Show grid
                reportDS = GetReportData();
                LoadResultsCombo();
                if (reportDS.Tables.Count > 0)
                {
                    ShowTable();
                }
                else
                {
                    MessageBox.Show("Report didn't return a result set", "Warning", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (SqlException ex) when (ex.Number == 201) // Parameter required
            {
                ShowParamPrompt(true); // Show parameter prompt instead of grid as we have required parameters that were not supplied
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error running report:" + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ShowTable()
        {
            if (reportDS.Tables.Count == 0) return;
            if (selectedTableIndex >= reportDS.Tables.Count)
            {
                suppressCboResultsIndexChanged = true;
                selectedTableIndex = 0;
                cboResults.SelectedIndex = 0;
                dgv.Columns.Clear();
                suppressCboResultsIndexChanged = false;
            }
            var dt = reportDS.Tables[selectedTableIndex];
            ConvertDateTimeColsToLocalTimeZone(dt);

            SetDataSource(dt);

            SetColumnLayout();
        }

        private void SetColumnLayout()
        {
            const int maxWidth = 400;
            if (report.CustomReportResults[selectedTableIndex].ColumnLayout.Count > 0)
            {
                dgv.LoadColumnLayout(report.CustomReportResults[selectedTableIndex].ColumnLayout);
            }
            else if (doAutoSize)
            {
                dgv.AutoResizeColumns();
                // Ensure column size is not excessive
                foreach (DataGridViewColumn column in dgv.Columns)
                {
                    if (column.Width <= maxWidth) continue;
                    column.Width = maxWidth;
                }
            }

            if (dgv.Rows.Count > 0)
            {
                doAutoSize = false;
            }
        }

        /// <summary>
        /// Run the query to get the data for the user custom report
        /// </summary>
        /// <returns></returns>
        private DataSet GetReportData()
        {
            using var cn = new SqlConnection(Common.ConnectionString);
            using var cmd = new SqlCommand(report.QualifiedProcedureName, cn) { CommandType = CommandType.StoredProcedure, CommandTimeout = Config.DefaultCommandTimeout };
            using var da = new SqlDataAdapter(cmd);

            // Add system parameters unless they are overridden by user supplied parameters for drill down reports
            var pInstanceIDs = customParams.FirstOrDefault(p => p.Param.ParameterName.Equals("@InstanceIDs", StringComparison.InvariantCultureIgnoreCase) && p.UseDefaultValue);
            if (pInstanceIDs != null)
            {
                pInstanceIDs.Param.Value = context.InstanceIDs.AsDataTable();
            }
            var pInstanceID = customParams.FirstOrDefault(p => p.Param.ParameterName.Equals("@InstanceID", StringComparison.InvariantCultureIgnoreCase) && p.UseDefaultValue);
            if (pInstanceID != null)
            {
                pInstanceID.Param.Value = context.InstanceID > 0 ? context.InstanceID : DBNull.Value;
            }
            var pDatabaseID = customParams.FirstOrDefault(p => p.Param.ParameterName.Equals("@DatabaseID", StringComparison.InvariantCultureIgnoreCase) && p.UseDefaultValue);
            if (pDatabaseID != null)
            {
                pDatabaseID.Param.Value = context.DatabaseID > 0 ? context.DatabaseID : DBNull.Value;
            }
            var pFromDate = customParams.FirstOrDefault(p => p.Param.ParameterName.Equals("@FromDate", StringComparison.InvariantCultureIgnoreCase) && p.UseDefaultValue);
            if (pFromDate != null)
            {
                pFromDate.Param.Value = DateRange.FromUTC;
            }

            var pToDate = customParams.FirstOrDefault(p => p.Param.ParameterName.Equals("@ToDate", StringComparison.InvariantCultureIgnoreCase) && p.UseDefaultValue);
            if (pToDate != null)
            {
                pToDate.Param.Value = DateRange.ToUTC;
            }

            var pObjectID = customParams.FirstOrDefault(p => p.Param.ParameterName.Equals("@ObjectID", StringComparison.InvariantCultureIgnoreCase) && p.UseDefaultValue);
            if (pObjectID != null)
            {
                pObjectID.Param.Value = context.ObjectID > 0 ? context.ObjectID : DBNull.Value;
            }

            // Add user supplied parameters
            foreach (var p in customParams.Where(p => !p.UseDefaultValue || CustomReport.SystemParamNames.Contains(p.Param.ParameterName, StringComparer.OrdinalIgnoreCase)))
            {
                cmd.Parameters.Add(p.Param.Clone());
            }

            var ds = new DataSet();
            da.Fill(ds);
            return ds;
        }

        private DBADashContext context;
        private CustomReport report;
        private List<CustomSqlParameter> customParams = new();

        public void SetContext(DBADashContext context)
        {
            SetContext(context, null);
        }

        public void SetContext(DBADashContext context, List<CustomSqlParameter> sqlParams)
        {
            doAutoSize = true;
            selectedTableIndex = 0;
            dgv.Columns.Clear();
            this.context = context;
            report = context.Report;
            customParams = sqlParams ?? report.GetCustomSqlParameters();
            tsParams.Enabled = customParams.Count > 0;
            tsConfigure.Visible = report.CanEditReport;
            lblDescription.Text = report.Description;
            statusStrip1.Visible = !string.IsNullOrEmpty(report.Description);
            lblDescription.Visible = !string.IsNullOrEmpty(report.Description);
            if (report.DeserializationException != null)
            {
                MessageBox.Show(
                    "An error occurred deserializing the report. Preferences have been reset.\n" +
                    report.DeserializationException.Message, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                report.DeserializationException = null;// Display the message once
            }
            InitializeContextMenu();
            AddPickers();
            RefreshData();
        }

        private void AddPickers()
        {
            tsParams.DropDownItems.Clear();
            if (report.Pickers == null)
            {
                tsParams.Click += TsParameters_Click;
                return;
            }
            tsParams.Click -= TsParameters_Click;

            var pickers = report.Pickers;
            if (customParams.Any(p => p.Param.ParameterName.Equals("@Top", StringComparison.InvariantCultureIgnoreCase) && p.Param.SqlDbType == SqlDbType.Int) && !pickers.Any(p => p.ParameterName.Equals("@Top", StringComparison.InvariantCultureIgnoreCase)))
            {
                pickers.Add(Picker.CreateTopPicker());
            }

            foreach (var picker in report.Pickers.OrderBy(p => p.Name))
            {
                var param = customParams.FirstOrDefault(p =>
                        p.Param.ParameterName.Equals(picker.ParameterName,
                            StringComparison.InvariantCultureIgnoreCase));
                if (param == null) continue;
                if (param.UseDefaultValue && !string.IsNullOrEmpty(picker.DefaultValue))
                {
                    param.Param.Value = picker.DefaultValue;
                    param.UseDefaultValue = false;
                }
                var pickerMenu = new ToolStripMenuItem(picker.Name);
                foreach (var itm in picker.PickerItems)
                {
                    var item = new ToolStripMenuItem(itm.Value) { Tag = itm.Key };
                    item.Checked = param.UseDefaultValue && string.IsNullOrEmpty(itm.Key) || !param.UseDefaultValue && param.Param.Value.Equals(itm.Key);
                    item.Click += (sender, e) => PickerItem_Click(sender, e, itm, picker.ParameterName);
                    pickerMenu.DropDownItems.Add(item);
                }

                tsParams.DropDownItems.Add(pickerMenu);
            }

            tsParams.DropDownItems.Add(new ToolStripSeparator());

            var tsParameters = new ToolStripMenuItem("Parameters");
            tsParameters.Click += TsParameters_Click;
            tsParams.DropDownItems.Add(tsParameters);
        }

        private void PickerItem_Click(object sender, EventArgs eventArgs, KeyValuePair<string, string> itm, string paramName)
        {
            var menu = (ToolStripMenuItem)sender;
            var param = customParams.First(p => p.Param.ParameterName.Equals(paramName, StringComparison.InvariantCultureIgnoreCase));
            if (string.IsNullOrEmpty(itm.Key))
            {
                param.UseDefaultValue = true;
            }
            else
            {
                param.Param.Value = itm.Key;
                param.UseDefaultValue = false;
            }

            if (menu.Owner != null)
            {
                foreach (var item in menu.Owner.Items.Cast<ToolStripMenuItem>())
                {
                    item.Checked = item == menu;
                }
            }

            RefreshData();
        }

        private void TsRefresh_Click(object sender, EventArgs e)
        {
            RefreshData();
        }

        private void TsCopy_Click(object sender, EventArgs e)
        {
            Common.CopyDataGridViewToClipboard(dgv);
        }

        private void TsExcel_Click(object sender, EventArgs e)
        {
            Common.PromptSaveDataGridView(dgv);
        }

        private void SetTitleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var title = report.ReportName;
            if (CommonShared.ShowInputDialog(ref title, "Update Title") != DialogResult.OK) return;
            try
            {
                report.ReportName = title;
                report.Update();
                OnReportNameChanged(EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving title: " + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        protected virtual void OnReportNameChanged(EventArgs e)
        {
            ReportNameChanged?.Invoke(this, e);
        }

        private void TsParameters_Click(object sender, EventArgs e)
        {
            PromptParams();
        }

        private void LnkParams_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            PromptParams();
        }

        private void PromptParams()
        {
            var frm = new ReportParams() { Params = customParams };
            frm.ShowDialog();
            if (frm.DialogResult == DialogResult.OK)
            {
                customParams = frm.Params;
                AddPickers();// Update checks on picker items
            }
            RefreshData();
        }

        private void TsCols_Click(object sender, EventArgs e)
        {
            dgv.PromptColumnSelection();
        }

        private void SaveLayoutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Save Layout (column visibility, order and size)?", "Save", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;
            report.CustomReportResults[selectedTableIndex].ColumnLayout = dgv.GetColumnLayout();
            report.Update();
        }

        private void ResetLayoutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Reset Layout (column visibility, order and size)?", "Reset", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;
            report.CustomReportResults[selectedTableIndex].ColumnLayout = new();
            report.Update();
            dgv.Columns.Clear();
            doAutoSize = true;
            RefreshData();
        }

        private void CboResults_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (suppressCboResultsIndexChanged) return;
            selectedTableIndex = cboResults.SelectedIndex;
            dgv.Columns.Clear();
            ShowTable();
        }

        private void RenameResultSetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var name = cboResults.SelectedItem.ToString();
            if (CommonShared.ShowInputDialog(ref name, "Enter name") == DialogResult.OK)
            {
                report.CustomReportResults[cboResults.SelectedIndex].ResultName = name;
                suppressCboResultsIndexChanged = true;
                cboResults.Items[selectedTableIndex] = name;
                suppressCboResultsIndexChanged = false;
                report.Update();
            }
        }

        private void SetDescriptionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var description = report.Description;
            if (CommonShared.ShowInputDialog(ref description, "Enter description") != DialogResult.OK) return;
            report.Description = description;
            report.Update();
            lblDescription.Text = report.Description;
            statusStrip1.Visible = !string.IsNullOrEmpty(report.Description);
            lblDescription.Visible = !string.IsNullOrEmpty(report.Description);
        }

        private void ScriptReportToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                ScriptReport();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error scripting report:" + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ScriptReport()
        {
            using var cn = new SqlConnection(Common.ConnectionString);
            var serverCn = new ServerConnection(cn);
            var server = new Server(serverCn);
            var db = server.Databases[cn.Database];

            var proc = db.StoredProcedures[report.ProcedureName, report.SchemaName];

            if (proc != null)
            {
                var options = new ScriptingOptions() { ScriptForCreateOrAlter = true, ScriptBatchTerminator = true, EnforceScriptingOptions = true }; /* EnforceScriptingOptions = true is required to generate CREATE OR ALTER */

                var parts = proc.Script(options);
                var sb = new StringBuilder();
                sb.AppendFormat("/*\n\t{0}\n\t{1}\n\n\tCustom report for DBA Dash.\n\thttp://dbadash.com\n\tGenerated: {2:yyyy-MM-dd HH:mm:ss} \n*/\n\n",
                    report.ReportName.Replace("*/", ""), report.Description?.Replace("*/", ""), DateTime.Now);
                foreach (var part in parts)
                {
                    sb.AppendLine(part);
                    sb.AppendLine("GO");
                }

                var meta = report.Serialize();
                sb.AppendLine();
                sb.AppendLine("/* Report customizations in GUI */");
                sb.AppendFormat("DELETE dbo.CustomReport\nWHERE SchemaName = '{0}'\nAND ProcedureName = '{1}'\n\n", report.SchemaName.SqlSingleQuote(), report.ProcedureName.SqlSingleQuote());
                sb.AppendLine("INSERT INTO dbo.CustomReport(SchemaName,ProcedureName,MetaData)");
                sb.AppendFormat("VALUES('{0}','{1}','{2}')", report.SchemaName.SqlSingleQuote(),
                    report.ProcedureName.SqlSingleQuote(), meta.SqlSingleQuote());

                var frm = new CodeViewer() { Code = sb.ToString() };
                frm.ShowDialog();
            }
            else
            {
                MessageBox.Show($"Unable to find procedure {report.QualifiedProcedureName}", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Dgv_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var colName = dgv.Columns[e.ColumnIndex].DataPropertyName;
            LinkColumnInfo linkColumnInfo = null;
            report.CustomReportResults[selectedTableIndex].LinkColumns?.TryGetValue(colName, out linkColumnInfo);
            try
            {
                linkColumnInfo?.Navigate(context, dgv.Rows[e.RowIndex], selectedTableIndex);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error navigating to link: " + ex.Message, "Error", MessageBoxButtons.OK,
                                       MessageBoxIcon.Error);
            }
        }

        private void Dgv_RowsAdded(object sender, DataGridViewRowsAddedEventArgs e)
        {
            report.CustomReportResults[selectedTableIndex].CellHighlightingRules.FormatRowsAdded(sender as DataGridView, e);
        }

        private void TsClearFilter_Click(object sender, EventArgs e)
        {
            if (dgv.DataSource is not DataView dv) return;
            dv.RowFilter = string.Empty;
            tsClearFilter.Enabled = false;
            tsClearFilter.Font = new Font(tsClearFilter.Font, FontStyle.Regular);
            tsClearFilter.ToolTipText = string.Empty;
        }
    }
}