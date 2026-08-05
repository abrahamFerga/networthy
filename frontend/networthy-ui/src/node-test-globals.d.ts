/**
 * The few Node APIs the vitest suites use, declared here instead of pulling in `@types/node`.
 *
 * `tsc -b` only type-checks `src`, and this is a browser app: adding `"node"` to tsconfig's
 * `types` would put Node's globals into every component's scope (`setTimeout` returning a
 * `Timeout` rather than a number, `Buffer`, `process`, …) to serve a handful of test lines.
 * Keeping the surface to exactly what the tests call means app code that reaches for Node still
 * fails to compile, which is the point.
 */
declare module "node:fs" {
  export function readFileSync(path: string, encoding: "utf8"): string;
}

declare module "node:path" {
  export function join(...segments: string[]): string;
}

declare const process: { cwd(): string };
