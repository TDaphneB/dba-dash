﻿using DBAChecks;
using ICSharpCode.TextEditor.Document;
using Microsoft.Reporting.WinForms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static DBAChecksGUI.DiffControl;

namespace DBAChecksGUI
{


    public partial class Main : Form
    {

        public Main()
        {
            InitializeComponent();
        }

        string connectionString = "Data Source=.;Initial Catalog=DBAChecksDB;Integrated Security=SSPI";

        Int32 currentSummaryPage = 1;
        Int32 currentSummaryPageSize = 100;


        private void addInstanes(string tagIds="")
        {
            tv1.Nodes.Clear();
            var root = new SQLTreeItem("DBAChecks", SQLTreeItem.TreeType.DBAChecksRoot);
            tv1.Nodes.Add(root);

            SqlConnection cn = new SqlConnection(connectionString);
            using (cn)
            {
                cn.Open();
                SqlCommand cmd = new SqlCommand(@"SELECT Instance
FROM dbo.Instances I
WHERE I.IsActive=1
GROUP BY Instance
ORDER BY Instance", cn);
                //cmd.CommandType = CommandType.StoredProcedure;
                //if (tagIds.Length > 0)
                //{
                //    cmd.Parameters.AddWithValue("TagIDs", tagIds);
                //}
                var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var n = new SQLTreeItem((string)rdr[0], SQLTreeItem.TreeType.Instance);
                    n.AddDummyNode();
                    root.Nodes.Add(n);                   
                }
            }
            root.Expand();
        }

        private void addDatabases(SQLTreeItem instanceNode)
        {
            SqlConnection cn = new SqlConnection(connectionString);
            using (cn)
            {
                cn.Open();
                SqlCommand cmd = new SqlCommand(@"SELECT D.DatabaseID,D.name,O.ObjectID
FROM dbo.Databases D
JOIN dbo.Instances I ON I.InstanceID = D.InstanceID
LEFT JOIN dbo.DBObjects O ON O.DatabaseID = D.DatabaseID AND O.ObjectType='DB'
WHERE I.IsActive=1
AND D.IsActive=1
AND D.source_database_id IS NULL
AND I.Instance = @Instance
ORDER BY D.Name
", cn);
            //    cmd.CommandType = CommandType.StoredProcedure;

                cmd.Parameters.AddWithValue("Instance",instanceNode.ObjectName);
                var systemNode = new SQLTreeItem("System Databases", SQLTreeItem.TreeType.Folder);
                instanceNode.Nodes.Add(systemNode);
                var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var n = new SQLTreeItem((string)rdr[1], SQLTreeItem.TreeType.Database);
                    n.DatabaseID = (Int32)rdr[0];
                    if (!rdr.IsDBNull(2))
                    {
                        n.ObjectID = (Int64)rdr[2];
                    }
                    n.AddDatabaseFolders();
                    if ((new string[] { "master", "model", "msdb" }).Contains((string)rdr[1]))
                    {
                        systemNode.Nodes.Add(n);
                    }
                    else
                    {
                        instanceNode.Nodes.Add(n);
                    }         
                }
            }
        }




        private void ExpandObjects(SQLTreeItem n)
        {
            SqlConnection cn = new SqlConnection(connectionString);
            using (cn)
            {
                cn.Open();
                SqlCommand cmd = new SqlCommand(@"SELECT ObjectID,ObjectType,SchemaName,ObjectName
FROM dbo.DBObjects
WHERE DatabaseID=@DatabaseID
AND IsActive=1
AND ObjectType IN(SELECT value FROM STRING_SPLIT(@Types,','))
ORDER BY SchemaName,ObjectName
", cn);
         
                cmd.Parameters.AddWithValue("DatabaseID", n.DatabaseID);
                cmd.Parameters.AddWithValue("Types", n.Tag);
                var rdr = cmd.ExecuteReader();
                
                while (rdr.Read())
                {
                    string type = ((string)rdr[1]).Trim();
                    var objN = new SQLTreeItem((string)rdr[3], (string)rdr[2], type);
                    objN.ObjectID = (Int64)rdr[0];
                    n.Nodes.Add(objN);
                }
            }

        }

        private Int64 currentObjectID;
        private Int32 currentPage=1;
        private Int32 currentPageSize=100;

        private void getHistory(Int64 ObjectID, Int32 PageNum=1)
        {
            diffSchemaSnapshot.OldText="";
            diffSchemaSnapshot.NewText = "";
            currentPageSize= Int32.Parse(tsPageSize.Text);
            SqlConnection cn = new SqlConnection(connectionString);
            using (cn)
            {
                cn.Open();
                string sql = @"WITH T AS (
SELECT O.ObjectName,O.SchemaName,O.ObjectType,H.SnapshotValidFrom,H.SnapshotValidTo,H.ObjectDateCreated,H.ObjectDateModified,H.DDLID,H2.DDLID AS DDLIDOld,CASE WHEN H2.DDLID IS NOT NULL THEN 'Modified' ELSE 'Created' END AS Action
FROM dbo.DDLHistory H
JOIN dbo.DBObjects O ON O.ObjectID = H.ObjectID
LEFT JOIN dbo.DDLHistory H2 ON H2.ObjectID = O.ObjectID AND H2.SnapshotValidTo = H.SnapshotValidFrom
WHERE H.ObjectID=@ObjectID
UNION ALL
SELECT O.ObjectName,O.SchemaName,O.ObjectType,H.SnapshotValidTo AS SnapshotValidFrom,x.SnapshotValidTo,NULL,NULL,NULL AS DDLID,H.DDLID AS DDLIDOld,'Dropped' AS Action
FROM dbo.DDLHistory H
JOIN dbo.DBObjects O ON O.ObjectID = H.ObjectID
OUTER APPLY(SELECT TOP(1) nH.SnapshotValidFrom AS SnapshotValidTo
			FROM dbo.DDLHistory nH 
			WHERE nH.ObjectID = H.ObjectID
			AND nH.SnapshotValidFrom> H.SnapshotValidTo
			ORDER BY nh.SnapshotValidFrom
			) x
WHERE H.ObjectID=@ObjectID
AND NOT EXISTS(SELECT 1 
				FROM dbo.DDLHistory H2 
				WHERE H.ObjectID = H2.ObjectID
				AND H.SnapshotValidTo = H2.SnapshotValidFrom
				)
AND H.SnapshotValidTo<>'9999-12-31 00:00:00.000'
)
SELECT T.ObjectName,
       T.SchemaName,
       T.ObjectType,
       T.SnapshotValidFrom,
       T.SnapshotValidTo,
       T.ObjectDateCreated,
       T.ObjectDateModified,
       T.DDLID,
       T.DDLIDOld,
	   T.Action
FROM T
ORDER BY T.SnapshotValidFrom DESC
OFFSET @PageSize* (@PageNumber-1) ROWS
FETCH NEXT @PageSize ROWS ONLY";
                SqlCommand cmd = new SqlCommand(sql, cn);
                cmd.Parameters.AddWithValue("ObjectID", ObjectID);
                cmd.Parameters.AddWithValue("PageSize", currentPageSize);
                cmd.Parameters.AddWithValue("PageNumber", PageNum);
                SqlDataAdapter da = new SqlDataAdapter(cmd);
                DataSet ds = new DataSet();
                da.Fill(ds);
                gvHistory.AutoGenerateColumns = false;
                gvHistory.DataSource = ds.Tables[0];
                currentObjectID = ObjectID;
                currentPage = PageNum;
                tsPageNum.Text = "Page " + PageNum;

                tsPrevious.Enabled = (PageNum > 1);
                tsNext.Enabled = ds.Tables[0].Rows.Count == currentPageSize;
             
            }
        }

        private void loadDDL(Int64 DDLID,Int64 DDLIDOld)
        {
            string newText = Common.DDL(DDLID,connectionString);
            string oldText = Common.DDL(DDLIDOld,connectionString);
            diffSchemaSnapshot.OldText = oldText;
            diffSchemaSnapshot.NewText = newText;
        }

        private DiffControl diffSchemaSnapshot = new DiffControl();

        private void Main_Load(object sender, EventArgs e)
        {
            splitSchemaSnapshot.Panel1.Controls.Add(diffSchemaSnapshot);
            diffSchemaSnapshot.Dock = DockStyle.Fill;

            string jsonPath = System.IO.Path.Combine(Application.StartupPath, "ServiceConfig.json");
            if (System.IO.File.Exists(jsonPath))
            {
                string jsonConfig = System.IO.File.ReadAllText(jsonPath);
                var cfg= CollectionConfig.Deserialize(jsonConfig);
                connectionString = cfg.DestinationConnection.ConnectionString;
            }
            addInstanes();
            
    
        }

        private void tv1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            var n = (SQLTreeItem)e.Node;
       
            if (n.ObjectID >0)
            {
                if (!tabs.TabPages.Contains(tabSchema))
                {
                    tabs.TabPages.Add(tabSchema);
                };
                getHistory(n.ObjectID);
            }
            else
            {
                if (tabs.TabPages.Contains(tabSchema))
                {
                    tabs.TabPages.Remove(tabSchema);
                };
            }
            if(n.Type== SQLTreeItem.TreeType.Database || n.Type== SQLTreeItem.TreeType.Instance)
            {
                if (!tabs.TabPages.Contains(tabSnapshotsSummary))
                {
                    tabs.TabPages.Insert(0, tabSnapshotsSummary);
                };
                loadSnapshots();
            }
            else
            {
                if (tabs.TabPages.Contains(tabSnapshotsSummary))
                {
                    tabs.TabPages.Remove(tabSnapshotsSummary);
                }
            }
        }




        private DataTable blockingDT()
        {
            return null;
        }

        private void tv1_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var n = (SQLTreeItem)e.Node;
            if (n.Nodes.Count == 1 && ((SQLTreeItem)n.Nodes[0]).Type == SQLTreeItem.TreeType.DummyNode)
            {
                n.Nodes.Clear();
                if (n.Type== SQLTreeItem.TreeType.Instance)
                {
                    addDatabases(n);
                }
                else
                {
                    ExpandObjects(n);
                }
               
            }
        }

        private void gvHistory_SelectionChanged(object sender, EventArgs e)
        {
            if (gvHistory.SelectedRows.Count == 1)
            {
                var row = (DataRowView)gvHistory.SelectedRows[0].DataBoundItem;
                Int64 ddlID;
                Int64 ddlIDOld;
                if (row["DDLID"] == DBNull.Value)
                {
                    ddlID = -1;
                }
                else
                {
                    ddlID = (Int64)row["DDLID"];
                }
                if (row["DDLIDOld"] == DBNull.Value)
                {
                    ddlIDOld = -1;
                }
                else
                {
                    ddlIDOld = (Int64)row["DDLIDOld"];
                }
                loadDDL(ddlID, ddlIDOld);
            }
        }

        private void tsNext_Click(object sender, EventArgs e)
        {
            getHistory(currentObjectID, currentPage+1);
        }

        private void tsPrevious_Click(object sender, EventArgs e)
        {
            getHistory(currentObjectID, currentPage - 1);
        }

        private void tsPageSize_Validated(object sender, EventArgs e)
        {
            if (Int32.Parse(tsPageSize.Text) != currentPageSize) {
                getHistory(currentObjectID, 1);
            }
        }

        private void tsPageSize_Validating(object sender, CancelEventArgs e)
        {
            Int32 i;
            Int32.TryParse(tsPageSize.Text, out i);
            if (i <= 0)
            {
                tsPageSize.Text = currentPageSize.ToString();
            }
        }

        private void gvSnapshots_SelectionChanged(object sender, EventArgs e)
        {
            if (gvSnapshots.SelectedRows.Count == 1)
            {
                var row = (DataRowView)gvSnapshots.SelectedRows[0].DataBoundItem;
                DateTime SnapshotDate = (DateTime)row["SnapshotDate"];
                Int32 DatabaseID = (Int32)row["DatabaseID"];
                SqlConnection cn = new SqlConnection(connectionString);
                using (cn)
                {
                    cn.Open();
                    string sql = @"WITH Hold AS (
	SELECT * 
	FROM dbo.DDLHistory H
	WHERE H.DatabaseID = @DatabaseID
	AND H.SnapshotValidTo = @SnapshotDate
),
Hnew AS (
	SELECT * 
	FROM dbo.DDLHistory H
	WHERE H.DatabaseID = @DatabaseID
	AND H.SnapshotValidFrom = @SnapshotDate
)
SELECT O.ObjectName,O.SchemaName,O.ObjectType, CASE WHEN Hold.ObjectID IS NULL THEN 'Created' WHEN Hnew.ObjectID IS NULL THEN 'Dropped' ELSE 'Modified' END AS Action,
		Hnew.DDLID NewDDLID,
		HOld.DDLID OldDDLID
FROM Hnew
FULL JOIN Hold ON Hold.ObjectID = Hnew.ObjectID
JOIN dbo.DBObjects O ON Hold.ObjectID = O.ObjectID OR Hnew.ObjectID= O.ObjectID";
                    SqlCommand cmd = new SqlCommand(sql, cn);

                     cmd.Parameters.AddWithValue("DatabaseID", DatabaseID);
                   
                   var p =cmd.Parameters.AddWithValue("SnapshotDate", SnapshotDate);
                    p.DbType = DbType.DateTime2;

                    SqlDataAdapter da = new SqlDataAdapter(cmd);
                    DataSet ds = new DataSet();
                    da.Fill(ds);

                    gvSnapshotsDetail.AutoGenerateColumns = false;
                    gvSnapshotsDetail.DataSource = ds.Tables[0];
                    
            
                    
             


                    //tsPageNum.Text = "Page " + pageNum;

                    //tsPrevious.Enabled = (PageNum > 1);
                    //tsNext.Enabled = ds.Tables[0].Rows.Count == pageSize;

                }
            }
        }

  

        private void gvSnapshotsDetail_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            var colCount = gvSnapshotsDetail.Columns.Count;

            if (e.ColumnIndex == colCount-1 || e.ColumnIndex == colCount - 2)
            {
                var row = (DataRowView)gvSnapshotsDetail.Rows[e.RowIndex].DataBoundItem;
                string ddl = "";
                if (row["NewDDLID"] != DBNull.Value)
                {
                    ddl = Common.DDL((Int64)row["NewDDLID"],connectionString);
                }
                string ddlOld = "";
                if (row["OldDDLID"] != DBNull.Value)
                {
                    ddlOld = Common.DDL((Int64)row["OldDDLID"],connectionString);
                }
                ViewMode mode = ViewMode.Diff;
                if (e.ColumnIndex == colCount - 2)
                {
                     mode = ViewMode.Code;
                }
                var frm = new Diff();
                frm.setText(ddlOld, ddl, mode);
                frm.Show();
            }
        }

        private void loadSnapshots(Int32 pageNum = 1)
        {
            var n = (SQLTreeItem)tv1.SelectedNode;
            currentSummaryPage = Int32.Parse(tsSummaryPageSize.Text);
            if (n.Type == SQLTreeItem.TreeType.Database || n.Type == SQLTreeItem.TreeType.Instance)
            {
                SqlConnection cn = new SqlConnection(connectionString);
                using (cn)
                {
                    cn.Open();
                    string sql = @"
SELECT ss.DatabaseID,
       ss.SnapshotDate,
       ss.ValidatedDate,
	   DATEDIFF(d,SnapshotDate,ValidatedDate) AS ValidForDays,
	   DATEDIFF(d,ss.ValidatedDate,GETUTCDATE()) AS DaysSinceValidation,
       ss.Created,
       ss.Modified,
       ss.Dropped,
       ss.DDLSnapshotOptionsID,
        D.Name as DB
FROM dbo.DDLSnapshots ss
JOIN dbo.Databases D ON ss.DatabaseID = D.DatabaseID
JOIN dbo.Instances I ON D.InstanceID = I.InstanceID
WHERE (d.DatabaseID=@DatabaseID OR @DatabaseID =-1)
AND I.Instance = @Instance
ORDER BY SnapshotDate DESC
OFFSET @PageSize* (@PageNumber-1) ROWS
FETCH NEXT @PageSize ROWS ONLY
OPTION(RECOMPILE)";
                    SqlCommand cmd = new SqlCommand(sql, cn);

                    cmd.Parameters.AddWithValue("DatabaseID", n.DatabaseID);
                    cmd.Parameters.AddWithValue("Instance", n.InstanceName);
                    cmd.Parameters.AddWithValue("PageSize", currentSummaryPage);
                    cmd.Parameters.AddWithValue("PageNumber", pageNum);
                    SqlDataAdapter da = new SqlDataAdapter(cmd);
                    DataSet ds = new DataSet();
                    da.Fill(ds);
                    gvSnapshots.AutoGenerateColumns = false;
                    gvSnapshots.DataSource = ds.Tables[0];

                    tsSummaryPageNum.Text = "Page " + pageNum;
                    tsSummaryBack.Enabled = (pageNum > 1);
                    tsSummaryNext.Enabled = ds.Tables[0].Rows.Count == currentSummaryPage;
                    currentSummaryPage = pageNum;

                }
            }
        }

        private void tsSummaryBack_Click(object sender, EventArgs e)
        {
            loadSnapshots(currentSummaryPage - 1);
        }

        private void tsSummaryNext_Click(object sender, EventArgs e)
        {
            loadSnapshots(currentSummaryPage + 1);
        }

        private void tsSummaryPageSize_Validated(object sender, EventArgs e)
        {
            if (Int32.Parse(tsSummaryPageSize.Text) != currentPageSize)
            {
                loadSnapshots(1);
            }
        }

        private void tsSummaryPageSize_Validating(object sender, CancelEventArgs e)
        {
            Int32 i;
            Int32.TryParse(tsSummaryPageSize.Text, out i);
            if (i <= 0)
            {
                tsSummaryPageSize.Text = currentSummaryPageSize.ToString();
            }
        }



        private void dBDiffToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var frm = new DBDiff();
            frm.ConnectionString = connectionString;
            var n = (SQLTreeItem)tv1.SelectedNode;
            frm.SelectedInstanceA = n.InstanceName;
            frm.SelectedDatabaseA = new DBDiff.DatabaseItem() { DatabaseID = n.DatabaseID, DatabaseName = n.DatabaseName };
            frm.ShowDialog();
        }
    }
}
