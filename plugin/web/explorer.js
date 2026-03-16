const API_BASE = (window.location.port === '8899')
  ? window.location.origin
  : 'http://localhost:8899';

async function fetchJson(path) {
  const r = await fetch(API_BASE + path);
  return r.json();
}

function esc(s) {
  const d = document.createElement('div');
  d.textContent = String(s);
  return d.innerHTML;
}

// -- Tree node builder --

function createTreeNode(labelHtml, opts = {}) {
  const { expandable = true, onExpand, suffix } = opts;

  const node = document.createElement('div');
  node.className = 'tree-node';

  const header = document.createElement('div');
  header.className = 'tree-header';

  const arrow = document.createElement('span');
  arrow.className = expandable ? 'tree-arrow' : 'tree-arrow leaf';
  arrow.textContent = '\u25B6';
  header.appendChild(arrow);

  const label = document.createElement('span');
  label.innerHTML = labelHtml;
  header.appendChild(label);

  if (suffix) {
    const suf = document.createElement('span');
    suf.innerHTML = suffix;
    suf.style.marginLeft = '8px';
    header.appendChild(suf);
  }

  node.appendChild(header);

  const children = document.createElement('div');
  children.className = 'tree-children';
  node.appendChild(children);

  let loaded = false;
  if (expandable) {
    header.addEventListener('click', () => {
      const isOpen = children.classList.contains('open');
      if (isOpen) {
        children.classList.remove('open');
        arrow.classList.remove('expanded');
      } else {
        children.classList.add('open');
        arrow.classList.add('expanded');
        if (!loaded && onExpand) {
          loaded = true;
          const status = document.createElement('div');
          status.className = 'tree-status';
          status.textContent = 'Loading...';
          children.appendChild(status);
          onExpand(children).then(() => {
            if (status.parentNode) status.parentNode.removeChild(status);
          }).catch(e => {
            status.textContent = 'Error: ' + e.message;
            status.className = 'error-msg';
            status.style.paddingLeft = '20px';
          });
        }
      }
    });
  }

  return { node, children };
}

function createLeaf(html) {
  const leaf = document.createElement('div');
  leaf.className = 'tree-leaf';
  leaf.innerHTML = html;
  return leaf;
}

// -- Object loading (UEVR API format) --

function objParams(address, kind, typeName) {
  return `address=${encodeURIComponent(address)}&kind=${encodeURIComponent(kind)}&typeName=${encodeURIComponent(typeName)}`;
}

async function loadObjectInto(container, address, kind, typeName) {
  const data = await fetchJson(`/api/explorer/object?${objParams(address, kind, typeName)}`);
  if (data.error) {
    container.appendChild(createLeaf(`<span class="error-msg">${esc(data.error)}</span>`));
    return;
  }

  // Internals
  const internals = createTreeNode('Internals', {
    onExpand: async (c) => {
      c.appendChild(createLeaf(`<span class="oe-type">${esc(data.className || typeName)}</span>`));
      c.appendChild(createLeaf(`Address: ${esc(data.address)}`));
      if (data.fullName) c.appendChild(createLeaf(`Full name: ${esc(data.fullName)}`));
      if (data.super) c.appendChild(createLeaf(`Super: <span class="oe-type">${esc(data.super)}</span>`));
    }
  });
  container.appendChild(internals.node);

  // Fields — UEVR returns fields as an object {name: value}, not an array
  if (data.fields && typeof data.fields === 'object') {
    const fieldEntries = Object.entries(data.fields);
    if (fieldEntries.length > 0) {
      const fieldsNode = createTreeNode(`Fields <span style="color:#8b949e">(${fieldEntries.length})</span>`, {
        onExpand: async (c) => {
          for (const [name, value] of fieldEntries) {
            renderFieldEntry(c, name, value, address, kind, typeName);
          }
        }
      });
      container.appendChild(fieldsNode.node);
    }
  }

  // Methods — UEVR uses {name, owner, params: [{name, type}]}
  if (data.methods && data.methods.length > 0) {
    const methodsNode = createTreeNode(`Methods <span style="color:#8b949e">(${data.methods.length})</span>`, {
      onExpand: async (c) => {
        for (const m of data.methods) {
          renderMethod(c, m, address, kind, typeName);
        }
      }
    });
    container.appendChild(methodsNode.node);
  }
}

// -- Field rendering (UEVR format) --

function isObjectRef(value) {
  return value && typeof value === 'object' && !Array.isArray(value) && value.address && value.className;
}

function isDelegateType(value) {
  return value && typeof value === 'object' && !Array.isArray(value) && value.type &&
    (value.type.includes('Delegate') || value.type.includes('delegate'));
}

function renderFieldEntry(container, name, value, parentAddress, parentKind, parentTypeName) {
  const nameLabel = `<span class="oe-field">${esc(name)}</span>`;

  if (isObjectRef(value)) {
    // Object reference — expandable, navigates to child
    const typeLabel = `<span class="oe-type">${esc(value.className)}</span>`;
    const refNode = createTreeNode(`${typeLabel} ${nameLabel}`, {
      onExpand: async (c) => {
        await loadObjectInto(c, value.address, 'uobject', value.className);
      }
    });
    container.appendChild(refNode.node);
  } else if (Array.isArray(value)) {
    // Array field
    if (value.length === 0) {
      container.appendChild(createLeaf(`${nameLabel} = <span class="oe-null">[] (empty)</span>`));
    } else {
      const arrayNode = createTreeNode(`${nameLabel} <span style="color:#8b949e">[${value.length}]</span>`, {
        onExpand: async (c) => {
          for (let i = 0; i < value.length; i++) {
            const el = value[i];
            if (isObjectRef(el)) {
              const elNode = createTreeNode(`[${i}] <span class="oe-type">${esc(el.className)}</span>`, {
                onExpand: async (c2) => {
                  await loadObjectInto(c2, el.address, 'uobject', el.className);
                }
              });
              c.appendChild(elNode.node);
            } else {
              c.appendChild(createLeaf(`[${i}] <span class="oe-value">${esc(JSON.stringify(el))}</span>`));
            }
          }
        }
      });
      container.appendChild(arrayNode.node);
    }
  } else if (isDelegateType(value)) {
    // Delegate — show type, not expandable
    container.appendChild(createLeaf(`${nameLabel} <span style="color:#8b949e">${esc(value.type)}</span>`));
  } else if (value && typeof value === 'object') {
    // Other object (struct, etc.) — show as expandable with inline fields
    const keys = Object.keys(value);
    const structNode = createTreeNode(`${nameLabel} <span style="color:#8b949e">{${keys.length}}</span>`, {
      onExpand: async (c) => {
        for (const [k, v] of Object.entries(value)) {
          renderFieldEntry(c, k, v, parentAddress, parentKind, parentTypeName);
        }
      }
    });
    container.appendChild(structNode.node);
  } else {
    // Primitive value — show inline
    const valStr = value === null || value === undefined
      ? '<span class="oe-null">null</span>'
      : `<span class="oe-value">${esc(value)}</span>`;
    container.appendChild(createLeaf(`${nameLabel} = ${valStr}`));
  }
}

// -- Method rendering (UEVR format) --

function renderMethod(container, m, parentAddress, parentKind, parentTypeName) {
  const nameLabel = `<span class="oe-method">${esc(m.name)}</span>`;
  const ownerLabel = m.owner ? ` <span style="color:#8b949e">[${esc(m.owner)}]</span>` : '';
  const params = (m.params || []).map(p =>
    `<span class="oe-type">${esc(p.type)}</span> ${esc(p.name || '')}`
  ).join(', ');

  const noParams = (m.params || []).length === 0;
  const isGetter = noParams && /^(Get|Is|Has|Can|Find|Was|Does|Should|Check)/.test(m.name);

  if (isGetter) {
    const methodNode = createTreeNode(`${nameLabel}(${params})${ownerLabel}`, {
      onExpand: async (c) => {
        await loadMethodResult(c, parentAddress, parentKind, parentTypeName, m.name);
      }
    });
    container.appendChild(methodNode.node);
  } else {
    const { node } = createTreeNode(`${nameLabel}(${params})${ownerLabel}`, { expandable: false });
    container.appendChild(node);
  }
}

async function loadMethodResult(container, parentAddress, parentKind, parentTypeName, methodName) {
  const data = await fetchJson(`/api/explorer/method?${objParams(parentAddress, parentKind, parentTypeName)}&methodName=${encodeURIComponent(methodName)}`);
  if (data.error) {
    container.appendChild(createLeaf(`<span class="error-msg">${esc(data.error)}</span>`));
    return;
  }

  const result = data.result;
  if (result === null || result === undefined) {
    container.appendChild(createLeaf(`Result: <span class="oe-null">null / void</span>`));
  } else if (isObjectRef(result)) {
    await loadObjectInto(container, result.address, 'uobject', result.className);
  } else if (typeof result === 'object') {
    // Struct result
    for (const [k, v] of Object.entries(result)) {
      renderFieldEntry(container, k, v, parentAddress, parentKind, parentTypeName);
    }
  } else {
    container.appendChild(createLeaf(`Result: <span class="oe-value">${esc(result)}</span>`));
  }
}

// -- Entry point --

let singletonEntries = [];

async function loadExplorer() {
  const tree = document.getElementById('explorer-tree');
  tree.innerHTML = '<div class="tree-status">Loading singletons...</div>';

  try {
    const data = await fetchJson('/api/explorer/singletons');
    tree.innerHTML = '';

    singletonEntries = data.singletons || [];
    renderSingletonList();
  } catch (e) {
    tree.innerHTML = `<span class="error-msg">Failed to load: ${esc(e.message)}</span>`;
  }
}

function renderSingletonList() {
  const tree = document.getElementById('explorer-tree');
  tree.innerHTML = '';
  const filter = document.getElementById('explorer-search').value.toLowerCase();

  const filtered = singletonEntries.filter(s => {
    const searchable = (s.name + ' ' + s.className + ' ' + (s.fullName || '')).toLowerCase();
    return searchable.includes(filter);
  });

  const group = createTreeNode(`<span class="tree-group-label">Singletons (${filtered.length})</span>`, {
    onExpand: async (c) => {
      for (const s of filtered) {
        const label = s.fullName
          ? `<span class="oe-type">${esc(s.className)}</span> <span style="color:#8b949e">${esc(s.name)}</span>`
          : `<span class="oe-type">${esc(s.className)}</span>`;
        const sNode = createTreeNode(label, {
          onExpand: async (c2) => {
            await loadObjectInto(c2, s.address, 'uobject', s.className);
          }
        });
        c.appendChild(sNode.node);
      }
    }
  });
  tree.appendChild(group.node);
}

document.getElementById('explorer-search').addEventListener('input', renderSingletonList);

loadExplorer();
