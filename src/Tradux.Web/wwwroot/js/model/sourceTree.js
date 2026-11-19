// Campos de origem: a árvore que POST /v1/design/source-schema devolve para o
// XSD de origem (seção 12: "navegar nas árvores de origem"). Módulo puro.
//
// Nó: { name, path, type, children?: Nó[], item?: Nó }
//   type: 'text' | 'date' | 'number' | 'object' | 'list'
//   path: caminho com ponto relativo ao escopo (ex.: "Tomador.Cnpj"), o mesmo
//         formato que o motor resolve (SourceDataTree.TryGetValue).
//   item: para listas, a estrutura de um item — é o escopo de um bloco
//         "for each"; inclui "$index" e, para listas de valores simples, o valor.

export const TYPE_LABEL = { text: 'texto', date: 'data', number: 'número', object: 'objeto', list: 'lista' };

const INDEX_FIELD = { name: '$index', path: '$index', type: 'number' };

/**
 * Escopo de campos para uma cadeia de blocos "for each": `collectionPaths`
 * são os caminhos de coleção do bloco mais externo ao mais interno, cada um
 * relativo ao escopo anterior. Devolve o nó cujos filhos são os campos
 * disponíveis, ou null se algum caminho não existir na árvore de origem.
 */
export function scopeNode(tree, collectionPaths = []) {
  let scope = tree;
  for (const path of collectionPaths) {
    const node = findNode(scope, path);
    if (node?.type === 'list' && node.item) scope = node.item;
    else if (node?.type === 'object') scope = rebase(node); // objeto único = 1 item (ADR-0020)
    else return null;
  }

  return scope;
}

// Objeto como item: os caminhos dos campos ficam relativos ao objeto.
function rebase(node) {
  const prefix = `${node.path}.`;
  const strip = (child) => ({
    ...child,
    path: child.path.startsWith(prefix) ? child.path.slice(prefix.length) : child.path,
    ...(child.children ? { children: child.children.map(strip) } : {}),
  });
  return { name: '(item)', path: '', type: 'object', children: [...(node.children ?? []).map(strip), INDEX_FIELD] };
}

export function findNode(scope, path) {
  if (!scope || !path) return null;
  let current = scope;
  for (const segment of path.split('.')) {
    const next = current.children?.find((c) => c.name === segment);
    if (!next) return null;
    current = next;
  }

  return current;
}

export function flatten(scope) {
  const out = [];
  const visit = (node) => {
    for (const child of node.children ?? []) {
      out.push(child);
      if (child.type === 'object') visit(child);
    }
  };
  visit(scope);
  return out;
}

/** Campos de valor simples de um escopo, para os seletores de campo. */
export const valueFields = (scope) => (scope ? flatten(scope)
  .filter((n) => n.type !== 'object' && n.type !== 'list')
  .map((n) => ({ path: n.path, label: `${n.path} (${TYPE_LABEL[n.type] ?? n.type})` })) : []);

/** Coleções para um bloco "for each": listas, e objetos como "1 item" (opção explícita do bloco, ADR-0020). */
export const collectionFields = (scope) => (scope ? flatten(scope)
  .filter((n) => n.type === 'list' || n.type === 'object')
  .map((n) => (n.type === 'object'
    ? { path: n.path, label: `${n.path} (objeto único: 1 item)`, single: true }
    : { path: n.path, label: n.path })) : []);

/** Dentro de um "for each" (cadeia não vazia), os campos da raiz da entrada também (FieldScope.Root, ADR-0020). */
export const withRootFields = (fields, chain, root) => (chain.length > 0 && root
  ? [...fields, ...valueFields(root).map((f) => ({ ...f, scope: 'root', label: `(raiz) ${f.label}` }))]
  : fields);
