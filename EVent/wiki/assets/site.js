(function(){
  // ---------- theme toggle
  var btn=document.getElementById('theme-btn');
  function currentTheme(){
    var t=document.documentElement.getAttribute('data-theme');
    if(t) return t;
    return window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';
  }
  if(btn) btn.addEventListener('click',function(){
    var n=currentTheme()==='dark'?'light':'dark';
    document.documentElement.setAttribute('data-theme',n);
    try{localStorage.setItem('evwiki-theme',n);}catch(e){}
  });

  // ---------- C# highlighter (small, regex based)
  var KW='abstract as async await base bool break byte case catch char class const continue decimal default delegate do double else enum event explicit false finally float for foreach get if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed set short sizeof static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while where yield'.split(' ');
  var kwset={};KW.forEach(function(k){kwset[k]=1;});
  function esc(s){return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
  function hl(src){
    var re=/(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|(\$?@?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)')|(\b\d[\d_]*(?:\.\d+)?[fFuUlLmM]*\b|\b0x[0-9a-fA-F]+\b)|([A-Za-z_][A-Za-z0-9_]*)/g;
    var out='',last=0,m;
    while((m=re.exec(src))){
      out+=esc(src.slice(last,m.index));
      if(m[1]) out+='<span class="tk-com">'+esc(m[1])+'</span>';
      else if(m[2]) out+='<span class="tk-str">'+esc(m[2])+'</span>';
      else if(m[3]) out+='<span class="tk-num">'+esc(m[3])+'</span>';
      else if(m[4]){
        var w=m[4];
        if(kwset[w]) out+='<span class="tk-kw">'+w+'</span>';
        else if(/^[A-Z]/.test(w)) out+='<span class="tk-typ">'+w+'</span>';
        else out+=w;
      }
      last=re.lastIndex;
    }
    return out+esc(src.slice(last));
  }
  document.querySelectorAll('pre code.lang-csharp').forEach(function(el){el.innerHTML=hl(el.textContent);});

  // ---------- copy buttons
  document.querySelectorAll('.code').forEach(function(box){
    var b=document.createElement('button');b.className='copy-btn';b.textContent='Copy';
    b.addEventListener('click',function(){
      var t=box.querySelector('pre').innerText;
      (navigator.clipboard?navigator.clipboard.writeText(t):Promise.reject()).then(function(){b.textContent='Copied';setTimeout(function(){b.textContent='Copy';},1200);},function(){b.textContent='Select + Ctrl+C';});
    });
    box.appendChild(b);
  });

  // ---------- table of contents
  var toc=document.getElementById('toc');
  var heads=[].slice.call(document.querySelectorAll('article h2[id], article h3[id]'));
  if(toc){
    if(heads.length<2){var a=document.querySelector('.toc');if(a)a.style.visibility='hidden';}
    heads.forEach(function(h){
      var li=document.createElement('li');li.className=h.tagName==='H3'?'l3':'l2';
      var a=document.createElement('a');a.href='#'+h.id;a.textContent=h.textContent;li.appendChild(a);toc.appendChild(li);
    });
    var links=toc.querySelectorAll('a');
    function onScroll(){
      var cur=null;
      for(var i=0;i<heads.length;i++){ if(heads[i].getBoundingClientRect().top<110) cur=i; }
      links.forEach(function(l,i){l.classList.toggle('active',i===cur);});
    }
    window.addEventListener('scroll',onScroll,{passive:true});onScroll();
  }

  // ---------- search
  var input=document.getElementById('search'),res=document.getElementById('search-results');
  var idx=window.EV_SEARCH||[];var sel=-1;
  function run(){
    var q=input.value.trim().toLowerCase();
    if(!q){res.hidden=true;return;}
    var terms=q.split(/\s+/);
    var hits=idx.map(function(it){
      var hay=(it.t+' '+it.s).toLowerCase(),score=0;
      for(var i=0;i<terms.length;i++){var p=hay.indexOf(terms[i]);if(p<0)return null;score+=(it.t.toLowerCase().indexOf(terms[i])===0?3:1)+(p<it.t.length?2:0);}
      if(!it.s) score+=2;
      return {it:it,score:score};
    }).filter(Boolean).sort(function(a,b){return b.score-a.score;}).slice(0,14);
    sel=-1;
    res.innerHTML=hits.length?hits.map(function(h){return '<a href="'+h.it.u+'">'+esc(h.it.t)+'<span class="sr-s">'+esc(h.it.s||'Page')+'</span></a>';}).join(''):'<div class="sr-empty">No matches</div>';
    res.hidden=false;
  }
  if(input){
    input.addEventListener('input',run);
    input.addEventListener('keydown',function(ev){
      var items=res.querySelectorAll('a');
      if(ev.key==='ArrowDown'){sel=Math.min(sel+1,items.length-1);ev.preventDefault();}
      else if(ev.key==='ArrowUp'){sel=Math.max(sel-1,0);ev.preventDefault();}
      else if(ev.key==='Enter'){if(items[sel<0?0:sel]) location.href=items[sel<0?0:sel].getAttribute('href');return;}
      else if(ev.key==='Escape'){res.hidden=true;input.blur();return;}
      items.forEach(function(a,i){a.classList.toggle('sel',i===sel);});
    });
    document.addEventListener('click',function(ev){if(!ev.target.closest('.search'))res.hidden=true;});
    document.addEventListener('keydown',function(ev){if(ev.key==='/'&&document.activeElement!==input&&!/INPUT|TEXTAREA/.test(document.activeElement.tagName)){ev.preventDefault();input.focus();}});
  }

  // ---------- issue filter (issues page only)
  var fb=document.querySelector('.filter-bar');
  if(fb){
    fb.addEventListener('click',function(ev){
      var b=ev.target.closest('button');if(!b)return;
      fb.querySelectorAll('button').forEach(function(x){x.classList.toggle('on',x===b);});
      var f=b.getAttribute('data-f');
      document.querySelectorAll('.issue').forEach(function(el){
        el.style.display=(f==='all'||el.classList.contains('sev-'+f)||el.classList.contains(f)||el.getAttribute('data-cat')===f)?'':'none';
      });
    });
  }
  // close mobile nav on navigation
  document.querySelectorAll('.sidebar a').forEach(function(a){a.addEventListener('click',function(){document.body.classList.remove('nav-open');});});
})();
