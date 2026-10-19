// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	site: 'https://fuseraft.ai',
	base: '/sql-mcp',
	integrations: [
		starlight({
			title: 'sql-mcp',
			description: 'An MCP server that lets AI agents read SQL Server, PostgreSQL, MySQL and Oracle databases, and nothing more.',
			logo: { src: './src/assets/sql-mcp.svg' },
			favicon: '/favicon.svg',
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/fuseraft/sql-mcp' }],
			editLink: { baseUrl: 'https://github.com/fuseraft/sql-mcp/edit/master/docs/' },
			lastUpdated: true,
			customCss: ['./src/styles/custom.css'],
			sidebar: [
				{
					label: 'Start here',
					items: ['start/install', 'start/quickstart', 'start/clients'],
				},
				{
					label: 'Guides',
					items: [
						'guides/read-only-logins',
						'guides/agents',
						'guides/deployment',
						'guides/logging',
						'guides/development',
					],
				},
				{
					label: 'Reference',
					items: ['reference/tools', 'reference/config', 'reference/http'],
				},
				{ label: 'How queries stay read-only', link: '/security/' },
			],
		}),
	],
});
