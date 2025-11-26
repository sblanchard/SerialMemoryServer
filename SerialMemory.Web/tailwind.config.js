/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        // Main theme colors (matching SaaS site)
        midnight: '#0B0F1A',
        navy: '#11162A',
        electric: '#3B82F6',
        cyan: '#22D3EE',
        graphite: '#6B7280',
        slate: '#374151',
        'soft-white': '#E5E7EB',
        success: '#22C55E',
        warning: '#FACC15',
        danger: '#EF4444',

        // Aliases for compatibility
        primary: {
          DEFAULT: '#3B82F6',
          hover: '#2563EB',
        },
        bg: {
          primary: '#0B0F1A',
          secondary: '#11162A',
          tertiary: '#1a1f3a',
        },

        // Entity colors for graph nodes
        entity: {
          person: '#3B82F6',    // Electric blue
          org: '#8B5CF6',       // Purple
          gpe: '#22C55E',       // Success green
          date: '#FACC15',      // Warning yellow
          email: '#22D3EE',     // Cyan
          url: '#EC4899',       // Pink
          title: '#6366F1',     // Indigo
          default: '#3B82F6',   // Electric blue
        }
      },
      fontFamily: {
        sans: ['Inter', 'system-ui', '-apple-system', 'sans-serif'],
        mono: ['JetBrains Mono', 'Fira Code', 'monospace'],
      },
      boxShadow: {
        'glow': '0 0 20px rgba(59, 130, 246, 0.15)',
        'glow-cyan': '0 0 20px rgba(34, 211, 238, 0.2)',
      }
    },
  },
  plugins: [],
}
