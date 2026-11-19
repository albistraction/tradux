// Tabelas DE-PARA (operador lookup). No editor cada tabela é uma lista
// ordenada de linhas [de, para]: editar a chave enquanto se digita não mistura
// linhas nem perde a posição. A API usa dicionário (de → para). Módulo puro.

/** Dicionários da API → linhas do editor. */
export const rowsFromApi = (tables) => Object.fromEntries(Object.entries(tables ?? {})
  .map(([name, rows]) => [name, Object.entries(rows ?? {}).map(([k, v]) => [String(k), String(v ?? '')])]));

/** Linhas do editor → dicionários da API; linha ainda sem chave fica de fora. */
export const tablesForApi = (tables) => Object.fromEntries(Object.entries(tables)
  .map(([name, rows]) => [name, Object.fromEntries(rows.filter(([key]) => key !== ''))]));

export function duplicateKeys(rows) {
  const seen = new Set();
  const duplicates = new Set();
  for (const [key] of rows) {
    if (key === '') continue; // linha ainda sem chave: ignorada no teste, não é repetição (R7)
    if (seen.has(key)) duplicates.add(key);
    seen.add(key);
  }

  return duplicates;
}

/** Uma mensagem por tabela com chave repetida (o dicionário da API perderia uma das linhas). */
export const duplicateProblems = (tables) => Object.entries(tables)
  .map(([name, rows]) => [name, duplicateKeys(rows)])
  .filter(([, duplicates]) => duplicates.size > 0)
  .map(([name, duplicates]) => `Tabela DE-PARA "${name}": chave repetida (${[...duplicates].join(', ')}).`);

export const validRows = (rows) => Array.isArray(rows)
  && rows.every((row) => Array.isArray(row) && row.length === 2 && row.every((cell) => typeof cell === 'string'));
