import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerBatchExportTool(server: McpServer) {
  server.tool(
    "batch_export",
    "Export multiple views/sheets to PDF, DWG, or IFC. PDF: one file per sheet named '<sheet number> - <sheet name>.pdf', printed at each sheet's own size. The folder is created if missing.",
    {
      format: z
        .enum(["PDF", "DWG", "IFC"])
        .describe("Export format"),
      sheetIds: z
        .array(z.number())
        .optional()
        .describe("Sheet IDs to export (for PDF/DWG). If empty, exports all sheets."),
      viewIds: z
        .array(z.number())
        .optional()
        .describe("View IDs to export (for DWG)"),
      exportPath: z
        .string()
        .optional()
        .describe("Directory path for exported files. If omitted, uses temp folder."),
      paperSize: z
        .enum(["Auto", "A4", "A3", "A2", "A1", "A0", "Letter", "Tabloid"])
        .optional()
        .describe("Paper size for PDF export. Default 'Auto' = each sheet's own size; a fixed size scales sheets to fit."),
    },
    async (args, extra) => {
      const params = {
        format: args.format,
        sheetIds: args.sheetIds ?? [],
        viewIds: args.viewIds ?? [],
        exportPath: args.exportPath ?? "",
        paperSize: args.paperSize ?? "Auto",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("batch_export", params);
        }, 300000);

        return rawToolResponse("batch_export", response);
      } catch (error) {
        return rawToolError("batch_export", `Batch export failed: ${
                errorMessage(error)
              }`);
      }
    }
  );
}
