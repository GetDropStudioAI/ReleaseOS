/** The short name (slug) suggested for a workspace name: lower case letters and digits joined by hyphens, at most 40 characters (the server's rule). */
export function slugFromName(name: string): string {
  return name.normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+/, '').slice(0, 40).replace(/-+$/, '')
}

/** The server's rule for the short name, so a bad one is shown before the request (the server still decides). */
export const slugProblem = (slug: string): string | null =>
  /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/.test(slug) && slug.length <= 40 ? null : 'Use lower case letters, digits and hyphens, starting and ending with a letter or digit (at most 40 characters).'
