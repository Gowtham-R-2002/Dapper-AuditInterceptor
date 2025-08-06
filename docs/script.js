$(document).ready(function() {
    // Smooth scrolling for navigation links
    $('.nav-link').on('click', function(e) {
        e.preventDefault();
        const target = $(this.getAttribute('href'));
        if (target.length) {
            $('html, body').animate({
                scrollTop: target.offset().top - 80
            }, 500);
        }
    });

    // Active navigation highlighting
    $(window).on('scroll', function() {
        const scrollTop = $(window).scrollTop();
        
        $('section').each(function() {
            const top = $(this).offset().top - 100;
            const bottom = top + $(this).outerHeight();
            
            if (scrollTop >= top && scrollTop < bottom) {
                const id = $(this).attr('id');
                $('.nav-link').removeClass('active');
                $(`.nav-link[href="#${id}"]`).addClass('active');
            }
        });
    });

    // Copy code blocks functionality
    $('.code-block pre').each(function() {
        const $pre = $(this);
        const $code = $pre.find('code');
        
        // Add copy button
        const $copyBtn = $('<button class="btn btn-sm btn-outline-secondary copy-btn" style="position: absolute; top: 5px; right: 5px;"><i class="fas fa-copy"></i></button>');
        $pre.css('position', 'relative');
        $pre.append($copyBtn);
        
        $copyBtn.on('click', function() {
            const textToCopy = $code.text();
            navigator.clipboard.writeText(textToCopy).then(function() {
                $copyBtn.html('<i class="fas fa-check"></i>');
                setTimeout(function() {
                    $copyBtn.html('<i class="fas fa-copy"></i>');
                }, 2000);
            });
        });
    });

    // Search functionality
    const $searchInput = $('<input type="text" class="form-control mb-3" placeholder="Search documentation..." id="searchInput">');
    $('.sidebar .p-3').prepend($searchInput);
    
    $('#searchInput').on('input', function() {
        const searchTerm = $(this).val().toLowerCase();
        
        if (searchTerm.length === 0) {
            $('.nav-link').show();
            return;
        }
        
        $('.nav-link').each(function() {
            const text = $(this).text().toLowerCase();
            if (text.includes(searchTerm)) {
                $(this).show();
            } else {
                $(this).hide();
            }
        });
    });

    // Tooltip initialization
    $('[data-bs-toggle="tooltip"]').tooltip();

    // Collapse/expand sections
    $('.card-header').on('click', function() {
        const $cardBody = $(this).next('.card-body');
        $cardBody.slideToggle();
    });

    // Syntax highlighting for code blocks
    if (typeof Prism !== 'undefined') {
        Prism.highlightAll();
    }

    // Mobile menu toggle
    $('.navbar-toggler').on('click', function() {
        $('.sidebar').toggleClass('show');
    });

    // Close mobile menu when clicking outside
    $(document).on('click', function(e) {
        if (!$(e.target).closest('.sidebar, .navbar-toggler').length) {
            $('.sidebar').removeClass('show');
        }
    });

    // Back to top button
    const $backToTop = $('<button class="btn btn-primary back-to-top" style="position: fixed; bottom: 20px; right: 20px; z-index: 1000; display: none;"><i class="fas fa-arrow-up"></i></button>');
    $('body').append($backToTop);
    
    $(window).on('scroll', function() {
        if ($(window).scrollTop() > 300) {
            $backToTop.fadeIn();
        } else {
            $backToTop.fadeOut();
        }
    });
    
    $backToTop.on('click', function() {
        $('html, body').animate({ scrollTop: 0 }, 500);
    });

    // Table of contents generation
    function generateTOC() {
        const $toc = $('<div class="toc-container mt-3"><h6>Table of Contents</h6><ul class="toc-list"></ul></div>');
        const $tocList = $toc.find('.toc-list');
        
        $('h5, h6').each(function() {
            const $heading = $(this);
            const text = $heading.text();
            const level = $heading.is('h5') ? 1 : 2;
            const $li = $(`<li class="toc-item toc-level-${level}"><a href="#${text.toLowerCase().replace(/\s+/g, '-')}">${text}</a></li>`);
            $tocList.append($li);
            
            // Add ID to heading
            $heading.attr('id', text.toLowerCase().replace(/\s+/g, '-'));
        });
        
        $('.card-body').prepend($toc);
    }
    
    // Uncomment to enable TOC generation
    // generateTOC();
}); 