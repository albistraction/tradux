// DE-PARA em edição: situação de cada nó de destino, escopo dos blocos
// "for each", validação e resumos. Módulo puro. A conversão para o JSON do
// motor fica em mappingTemplate.js.
//
// A árvore de destino vem de POST /v1/design/schema (DestinationNode). Os
// mapeamentos ficam num mapa por caminho de destino (node.path):
//   escalar:            { value: ValueSpec, condition?: Group, defaultValue?: ValueSpec(literal) }
//   complexo repetível: { forEach: { collectionPath, singleObject?: bool, filter?: Group, orderBy?: ValueSpec } }
//     singleObject: a origem é um objeto (não lista) e conta como 1 item (ADR-0020).
// Complexo não repetível não tem entrada: é gerado quando algum filho é mapeado.

import { validate, describe } from './ruleNodes.js';
import { validateCondition, describeCondition } from './conditions.js';

// Elemento cujo conteúdo é xs:choice (ramos como filhos diretos, sem nó de grupo): cada ramo é
// opcional; o choice exige ao menos um ramo mapeado (ADR-0018). Ex.: Rps do webserviceNFSe.
export const isChoiceContent = (node) => node.contentModel === 'choice';

export const isRepeatable = (node) => node.maxOccurs === null || node.maxOccurs === undefined || node.maxOccurs > 1;

/**
 * Coleção de origem de um bloco "for each". Objeto no lugar de lista (ex.: um
 * documento → uma ocorrência) só com a opção explícita de objeto único, nunca
 * implícita (ADR-0020); o resto do bloco (filtro, ordem) é mantido.
 */
export function withCollection(forEach, collectionPath, singleObject) {
  const { singleObject: _previous, ...rest } = forEach ?? {};
  return singleObject ? { ...rest, collectionPath, singleObject: true } : { ...rest, collectionPath };
}

/**
 * Situação de cada nó (para marcação visual, seção 12: "visualizar
 * obrigatórios sem mapeamento"). Devolve Map(path → status):
 *   'mapped' | 'missing' (obrigatório sem mapeamento) | 'optional' |
 *   'default' (obrigatório coberto pelo valor padrão do schema) |
 *   'container' (complexo ou grupo) | 'unsupported' (conteúdo que o motor não gera) |
 *   'blocked' (obrigatório com conteúdo que o motor não gera) |
 *   'signature' (ds:Signature: quem chama o serviço assina) |
 *   'inactive' (mapeado dentro de bloco for each sem coleção — será ignorado).
 * Um filho só é exigido se o pai "existe": raiz, complexo obrigatório ou
 * complexo com algum mapeamento, bloco for each configurado.
 */
export function computeStatus(root, mappings) {
  const status = new Map();

  // parentActive: o pai existe no XML (então obrigatórios são exigidos).
  // detached: está dentro de um bloco "for each" sem coleção — o que estiver
  // mapeado aqui será ignorado pelo motor (revisão da Fase 6, A5).
  const visit = (node, parentActive, detached) => {
    if (node.signedByCaller) {
      status.set(node.path, 'signature');
      return false;
    }

    if (!node.mappable) {
      // Obrigatório que o motor não sabe gerar (conteúdo opaco): bloqueia a publicação (B2).
      status.set(node.path, parentActive && node.minOccurs >= 1 ? 'blocked' : 'unsupported');
      (node.children ?? []).forEach((c) => visit(c, false, detached));
      return false;
    }

    if (node.kind === 'Group') {
      // Ramos de choice não são obrigatórios um a um; o choice obrigatório exige um ramo mapeado.
      const choice = node.contentModel === 'choice';
      const groupActive = parentActive && !choice && node.minOccurs >= 1;
      const anyMapped = (node.children ?? []).map((c) => visit(c, groupActive, detached)).some(Boolean);
      status.set(node.path, choice && parentActive && node.minOccurs >= 1 && !anyMapped ? 'missing' : 'container');
      return anyMapped;
    }

    if (node.kind === 'Scalar') {
      const mapped = Boolean(mappings[node.path]?.value);
      const required = parentActive && node.minOccurs >= 1;
      status.set(node.path, mapped
        ? (detached ? 'inactive' : 'mapped')
        : required ? (node.defaultValue != null ? 'default' : 'missing') : 'optional');
      return mapped && !detached;
    }

    if (isRepeatable(node)) {
      const configured = Boolean(mappings[node.path]?.forEach?.collectionPath);
      const required = parentActive && node.minOccurs >= 1;
      const branchMapped = (node.children ?? []).map((c) => visit(c, configured && !detached && !isChoiceContent(node), detached || !configured)).some(Boolean);
      status.set(node.path, configured
        ? (detached ? 'inactive' : isChoiceContent(node) && !branchMapped ? 'missing' : 'mapped')
        : required ? 'missing' : 'optional');
      return configured && !detached;
    }

    // Complexo não repetível: ativo se obrigatório sob pai ativo, ou se algum descendente está mapeado.
    const selfRequired = node === root || (parentActive && node.minOccurs >= 1);
    const anyDescendantMapped = (node.children ?? []).some((c) => subtreeHasMapping(c, mappings));
    const active = selfRequired || anyDescendantMapped;
    const branchMapped = (node.children ?? []).map((c) => visit(c, active && !detached && !isChoiceContent(node), detached)).some(Boolean);
    status.set(node.path, isChoiceContent(node) && active && !detached && !branchMapped ? 'missing' : 'container');
    return anyDescendantMapped;
  };

  visit(root, true, false);
  return status;
}

/** Contagem por situação, para a barra de status do editor. */
export function statusCounts(root, mappings) {
  const counts = { missing: 0, blocked: 0, inactive: 0 };
  for (const s of computeStatus(root, mappings).values()) {
    if (s in counts) counts[s]++;
  }

  return counts;
}

export function removeSubtree(mappings, path) {
  return Object.fromEntries(Object.entries(mappings).filter(([p]) => p !== path && !p.startsWith(`${path}/`)));
}

export function subtreeHasMapping(node, mappings) {
  if (!node.mappable) return false;
  if (node.kind === 'Group') return (node.children ?? []).some((c) => subtreeHasMapping(c, mappings));
  if (node.kind === 'Scalar') return Boolean(mappings[node.path]?.value);
  if (isRepeatable(node)) return Boolean(mappings[node.path]?.forEach?.collectionPath);
  return (node.children ?? []).some((c) => subtreeHasMapping(c, mappings));
}

/**
 * Cadeia de coleções de origem que define o escopo de um nó de destino:
 * um caminho por bloco "for each" ancestral (do mais externo ao mais interno).
 * Para o próprio bloco, o escopo é o do pai (onde a coleção é escolhida).
 */
export function scopeChain(root, mappings, path) {
  const chain = [];
  const walk = (node) => {
    if (node.path === path) return true;
    for (const child of node.children ?? []) {
      if (child.kind !== 'Group' && path !== child.path && !path.startsWith(`${child.path}/`)) continue;
      const repeatableBlock = child.kind === 'Complex' && isRepeatable(child);
      if (repeatableBlock && child.path !== path) {
        const collectionPath = mappings[child.path]?.forEach?.collectionPath;
        if (collectionPath) chain.push(collectionPath);
        else chain.push(null);
      }

      if (walk(child)) return true;
      if (repeatableBlock && child.path !== path) chain.pop();
    }

    return false;
  };

  walk(root);
  return chain;
}

export function findDestination(root, path) {
  if (root.path === path) return root;
  for (const child of root.children ?? []) {
    // Grupo (sequence/choice/all) é transparente: os filhos têm o caminho do elemento pai, não o do grupo.
    if (child.kind === 'Group' || path === child.path || path.startsWith(`${child.path}/`)) {
      const found = findDestination(child, path);
      if (found) return found;
    }
  }

  return null;
}

/** Problemas locais de todos os mapeamentos (antes de chamar a API). */
export function validateDraft(root, mappings, operators) {
  const problems = [];
  for (const [path, entry] of Object.entries(mappings)) {
    const node = findDestination(root, path);
    if (!node) continue;
    const label = node.name;
    if (entry.value) problems.push(...validate(entry.value, operators, `${label} › valor`));
    if (entry.condition) problems.push(...validateCondition(entry.condition, operators, `${label} › condição`));
    if (entry.forEach) {
      if (!entry.forEach.collectionPath) problems.push(`${label} › for each: escolha a coleção de origem.`);
      if (entry.forEach.filter) problems.push(...validateCondition(entry.forEach.filter, operators, `${label} › filtro`));
    }
  }

  return problems;
}

/** Resumo de um mapeamento para mostrar ao lado do nó. */
export function summarize(entry) {
  if (!entry) return '';
  if (entry.forEach) {
    const filter = describeCondition(entry.forEach.filter);
    return `para cada item de ${entry.forEach.collectionPath || '?'}${filter ? ` onde ${filter}` : ''}`;
  }

  if (!entry.value) return '';
  const condition = describeCondition(entry.condition);
  const fallback = entry.defaultValue ? ` (padrão ${describe(entry.defaultValue)})` : '';
  return `${describe(entry.value)}${fallback}${condition ? ` se ${condition}` : ''}`;
}

export function pruneMappings(root, mappings) {
  return Object.fromEntries(Object.entries(mappings).filter(([path]) => findDestination(root, path)));
}
