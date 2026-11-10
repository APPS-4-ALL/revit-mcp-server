import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { addSuggestions, suggestIf } from "../utils/suggestions.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerBatchCreateSheetsTool(server: McpServer) {
  server.tool(
    "batch_create_sheets",
    "Create multiple sheets at once and place views on them, centered on the title block. Each sheet can have its own number, name, title block, and views to place.\n\nGUIDANCE:\n- \"A sheet per floor with its plan\": one entry per level, e.g. {number:'A-101', name:'01 - Entry Level', viewNames:['01 - Entry Level']}. Floor plans are usually named like their level (see get_project_info levels).\n- Views can be given by viewNames (exact view name, floor plans preferred) or viewIds.\n- Title block: omit to auto-pick the smallest one the views fit on at their scale.\n- A view already on another sheet is duplicated with its annotations as '<view> - <sheet number>' (ifViewOnAnotherSheet='skip' to leave it out).\n- The first new sheet is opened (open=false to stay in the current view).\n\nTIPS:\n- Sheet numbers must be unique — check existing sheets first\n- To export the new sheets, pass their sheetIds from this result to batch_export",
    {
      sheets: z
        .array(
          z.object({
            number: z.string().describe("Sheet number (e.g. 'A-101')"),
            name: z.string().describe("Sheet name (e.g. 'Floor Plan - Level 1')"),
            titleBlockName: z
              .string()
              .optional()
              .describe("Title block family type name for this sheet"),
            viewIds: z
              .array(z.number())
              .optional()
              .describe("View IDs to place on this sheet"),
            viewNames: z
              .array(z.string())
              .optional()
              .describe("View names to place on this sheet (exact names, e.g. '01 - Entry Level')"),
          })
        )
        .describe("Array of sheet definitions to create"),
      defaultTitleBlockName: z
        .string()
        .optional()
        .describe("Default title block for sheets that don't specify one. Omit to auto-fit."),
      ifViewOnAnotherSheet: z
        .enum(["duplicate", "skip"])
        .optional()
        .describe("When a view is already on another sheet: 'duplicate' (default) places a copy, 'skip' leaves it out."),
      open: z
        .boolean()
        .optional()
        .describe("Open the first created sheet. Default: true"),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("batch_create_sheets", args);
        });

        const data: any = typeof response === 'object' && response !== null ? response : {};
        const payload = data.Response ?? data.response ?? data;
        const sheetsCreated = payload.totalCreated ?? payload.sheetsCreated ?? 0;
        const anyViewPlaced = (payload.sheets ?? []).some((s: any) => (s.viewsPlaced ?? []).some((v: any) => v.success));

        const enriched = addSuggestions(response, [
          suggestIf(sheetsCreated > 0 && !anyViewPlaced, "Place views on the new sheets", "Sheets are empty — pass viewNames/viewIds or use place_viewport"),
          suggestIf(sheetsCreated > 0, "Export the new sheets", "batch_export format='PDF' with these sheetIds"),
        ]);

        return rawToolResponse("batch_create_sheets", enriched);
      } catch (error) {
        return rawToolError("batch_create_sheets", `Batch create sheets failed: ${errorMessage(error)}`);
      }
    }
  );
}
