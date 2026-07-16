import plenipoPreset from "@plenipo/ui/tailwind-preset";

/**
 * Tailwind must SEE the class names it should generate: our own src plus @plenipo/ui's built
 * library output (the shell's components carry their classes there). The dist path resolves
 * through pnpm's symlink because it's a literal prefix, not a glob over node_modules.
 * @type {import('tailwindcss').Config}
 */
export default {
  presets: [plenipoPreset],
  darkMode: "class",
  content: ["./index.html", "./src/**/*.{ts,tsx}", "./node_modules/@plenipo/ui/dist/*.js"],
  theme: {
    extend: {},
  },
  plugins: [],
};
