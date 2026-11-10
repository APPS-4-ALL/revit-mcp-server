import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerApplyColorFillSchemeTool(server: McpServer) {
  server.tool(
    "apply_color_fill_scheme",
    `Color rooms (or areas/spaces) in a plan with a native Revit Color Fill Scheme and place a real Color Fill Legend next to the plan. Preferred over color_elements/create_color_legend for presentation plans: no per-element overrides, and the legend updates live with the model.

GUIDANCE:
- "Color rooms by use/function/department" (any language): call with no parameterName. It picks the first populated of Department, Occupancy, Name.
- Works on the active view unless viewId is given; the view must be a plan that shows the rooms.
- Colors are deterministic: values are grouped by function (circulation, offices, learning, meeting, food, wet areas, services) with a muted palette per group. Override specific values with customColors.
- Re-running is safe: the scheme is reused/updated and the legend in that view is replaced.
- hideFloors (default true) hides the Floors category in the view so floor finish hatching does not show through the colors.`,
    {
      parameterName: z.string().optional().describe(
        "Text parameter to color by (e.g. 'Department', 'Name'). Omit to color by use (Department, then Occupancy, then Name)."
      ),
      categoryName: z.enum(["Rooms", "Areas", "Spaces"]).optional().describe("Default: Rooms."),
      viewId: z.number().optional().describe("Plan view to apply to. Default: the active view."),
      schemeName: z.string().optional().describe("Scheme name and legend title. Default: 'Room Use' (or 'Room <Parameter>')."),
      placeLegend: z.boolean().optional().describe("Place a Color Fill Legend in the view. Default: true."),
      legendPosition: z.enum(["right", "left", "above", "below"]).optional().describe("Legend position relative to the plan. Default: right."),
      hideFloors: z.boolean().optional().describe("Hide the Floors category in the view for clean flat colors. Default: true."),
      zoomToFit: z.boolean().optional().describe("Zoom the view to fit afterwards so plan and legend are both on screen (if the view is open). Default: true."),
      customColors: z.array(z.object({
        value: z.string().describe("Parameter value to match (case-insensitive)."),
        r: z.number().int().min(0).max(255),
        g: z.number().int().min(0).max(255),
        b: z.number().int().min(0).max(255),
      })).optional().describe("Explicit colors for specific values; all other values use the built-in palette."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("apply_color_fill_scheme", {
            parameterName: args.parameterName ?? "",
            categoryName: args.categoryName ?? "Rooms",
            viewId: args.viewId ?? 0,
            schemeName: args.schemeName ?? "",
            placeLegend: args.placeLegend ?? true,
            legendPosition: args.legendPosition ?? "right",
            hideFloors: args.hideFloors ?? true,
            zoomToFit: args.zoomToFit ?? true,
            customColors: args.customColors ?? [],
          });
        });
        return rawToolResponse("apply_color_fill_scheme", response);
      } catch (error) {
        return rawToolError("apply_color_fill_scheme", `Apply color fill scheme failed: ${errorMessage(error)}`);
      }
    }
  );
}
